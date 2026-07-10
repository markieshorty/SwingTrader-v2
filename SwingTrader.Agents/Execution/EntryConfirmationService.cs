using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Agents.Execution;

public enum EntryConfirmationVerdict { Confirmed, Rejected, Unavailable }

public sealed record EntryConfirmationResult(EntryConfirmationVerdict Verdict, string? Reason)
{
    public static readonly EntryConfirmationResult Confirmed = new(EntryConfirmationVerdict.Confirmed, null);
    public static EntryConfirmationResult Rejected(string reason) => new(EntryConfirmationVerdict.Rejected, reason);
    public static EntryConfirmationResult Unavailable(string reason) => new(EntryConfirmationVerdict.Unavailable, reason);
}

public interface IEntryConfirmationService
{
    /// <summary>
    /// Moment-of-purchase sanity gate using IEX intraday bars. Returns
    /// Unavailable (never Rejected) on any data problem - a feed outage must
    /// never halt trading; the caller fails open and buys exactly as before.
    /// </summary>
    Task<EntryConfirmationResult> ConfirmAsync(
        ITiingoClient tiingo, string symbol, decimal scoredPrice, decimal stopLossPrice, CancellationToken ct);
}

// Rejects entries whose setup died between pre-market scoring and the 9:20+
// fill: gapped too far up (chasing a move that already happened), fell
// through its own stop (buying an instant stop-out), or opened dead
// volume-wise. All thresholds live in ExecutionConfig; the whole gate is
// dormant unless Execution:IntradayConfirmationEnabled is true.
public class EntryConfirmationService(
    IOptions<ExecutionConfig> config,
    ILogger<EntryConfirmationService> logger) : IEntryConfirmationService
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    // Baseline window: enough calendar days to hold 20 trading days of
    // 60-minute bars for the average-IEX-daily-volume denominator.
    private const int BaselineCalendarDays = 30;
    private const int BaselineTradingDays = 20;

    public async Task<EntryConfirmationResult> ConfirmAsync(
        ITiingoClient tiingo, string symbol, decimal scoredPrice, decimal stopLossPrice, CancellationToken ct)
    {
        try
        {
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);

            // Today's 5-minute bars: freshest price + cumulative session volume.
            var todayBars = await tiingo.GetIexIntradayAsync(symbol, nowEt.ToString("yyyy-MM-dd"));

            // Volume baseline from the SAME source: IEX volume is a small,
            // symbol-varying fraction of consolidated volume (AAPL ~2-4%), so
            // comparing an IEX session against the consolidated 20-day average
            // from our daily candles would reject every entry, every day.
            // Summing 60-minute IEX bars per day gives a like-for-like
            // denominator in one call.
            var hourlyBars = await tiingo.GetIexIntradayAsync(
                symbol, nowEt.AddDays(-BaselineCalendarDays).ToString("yyyy-MM-dd"), "60min", "volume");

            var avgIexDailyVolume = AverageDailyVolume(hourlyBars, excludeDate: DateOnly.FromDateTime(nowEt));

            return Evaluate(todayBars, scoredPrice, stopLossPrice, avgIexDailyVolume, nowEt, config.Value);
        }
        catch (Exception ex)
        {
            // Includes unknown-on-IEX tickers ({"detail":"Not found."} fails
            // List<> deserialization) and any transport error.
            logger.LogWarning(ex, "Entry confirmation unavailable for {Symbol} — failing open", symbol);
            return EntryConfirmationResult.Unavailable(ex.Message);
        }
    }

    // Average full-day IEX volume over the most recent BaselineTradingDays
    // days (today excluded - it's the partial session being judged).
    internal static decimal? AverageDailyVolume(IReadOnlyList<TiingoIexPrice> hourlyBars, DateOnly excludeDate)
    {
        var byDay = hourlyBars
            .GroupBy(b => DateOnly.FromDateTime(b.Date))
            .Where(g => g.Key != excludeDate)
            .OrderByDescending(g => g.Key)
            .Take(BaselineTradingDays)
            .Select(g => g.Sum(b => b.Volume ?? 0m))
            .ToList();

        return byDay.Count > 0 ? byDay.Average() : null;
    }

    // The pure gate. Bars must be today's session, oldest-first (as the API
    // returns them). Any reason to distrust the data -> Unavailable, never
    // Rejected.
    internal static EntryConfirmationResult Evaluate(
        IReadOnlyList<TiingoIexPrice> todayBars,
        decimal scoredPrice,
        decimal stopLossPrice,
        decimal? avgIexDailyVolume,
        DateTime nowEt,
        ExecutionConfig cfg)
    {
        if (todayBars.Count == 0)
            return EntryConfirmationResult.Unavailable("No IEX bars for today's session");

        var latest = todayBars[^1].Close ?? todayBars[^1].Open;
        var sessionOpen = todayBars[0].Open ?? todayBars[0].Close;
        if (latest is not > 0m || sessionOpen is not > 0m || scoredPrice <= 0m)
            return EntryConfirmationResult.Unavailable("IEX bars carry no usable prices");

        // Gap-up gate: if the stock already ran more than MaxGapUpPct past the
        // price the signal was scored at, the setup has priced in - we'd be
        // chasing. Judged on the worse of the session open and the latest
        // price (a gap that faded back below threshold passes).
        var reference = Math.Max(sessionOpen.Value, latest.Value);
        var gapPct = (reference - scoredPrice) / scoredPrice * 100m;
        if (gapPct > cfg.MaxGapUpPct)
        {
            return EntryConfirmationResult.Rejected(
                $"gapped {gapPct:F1}% above the scored price (${scoredPrice:F2} → ${reference:F2}, limit {cfg.MaxGapUpPct:F1}%)");
        }

        // Gap-down gate: price below the freshly derived stop level means the
        // entry would be an instant stop-out.
        if (latest.Value < stopLossPrice)
        {
            return EntryConfirmationResult.Rejected(
                $"trading at ${latest.Value:F2}, below the stop level ${stopLossPrice:F2} — instant stop-out");
        }

        // Volume gate: dead-on-arrival session. Only after VolumeGateEarliestEt
        // (too little data before then) and only when a same-source baseline
        // exists.
        var gateOpens = nowEt.Date
            .AddHours(cfg.VolumeGateEarliestHourEt)
            .AddMinutes(cfg.VolumeGateEarliestMinuteEt);
        if (nowEt >= gateOpens && avgIexDailyVolume is > 0m)
        {
            var sessionVolume = todayBars.Sum(b => b.Volume ?? 0m);
            var ratio = sessionVolume / avgIexDailyVolume.Value;
            if (ratio < cfg.MinSessionVolumeRatio)
            {
                return EntryConfirmationResult.Rejected(
                    $"session volume is only {ratio:P0} of a typical day (IEX), below the {cfg.MinSessionVolumeRatio:P0} dead-on-arrival floor");
            }
        }

        return EntryConfirmationResult.Confirmed;
    }
}
