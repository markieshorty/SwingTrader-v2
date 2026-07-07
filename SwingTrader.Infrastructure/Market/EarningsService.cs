using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Infrastructure.Market;

public class EarningsService(
    IRateLimiter rateLimiter,
    IMemoryCache cache,
    IOptions<EarningsConfig> config,
    ILogger<EarningsService> logger) : IEarningsService
{
    private static readonly EarningsContext NoneContext = new(
        EarningsSetupType.None, false, null, false, null, null, false, null);

    public async Task<EarningsContext> GetEarningsContextAsync(IFinnhubClient finnhub, string symbol, CancellationToken ct, int? gateDays = null)
    {
        var effectiveGateDays = gateDays ?? config.Value.GateDays;
        var cacheKey = $"earnings_{symbol}_{effectiveGateDays}";
        if (cache.TryGetValue(cacheKey, out EarningsContext? cached) && cached is not null)
            return cached;

        var result = await FetchContextAsync(finnhub, symbol, effectiveGateDays, ct);
        cache.Set(cacheKey, result, TimeSpan.FromHours(6));
        return result;
    }

    private async Task<EarningsContext> FetchContextAsync(IFinnhubClient finnhub, string symbol, int gateDays, CancellationToken ct)
    {
        var cfg = config.Value;
        var today = DateTime.UtcNow.Date;

        try
        {
            var from = today.ToString("yyyy-MM-dd");
            var to = today.AddDays(7).ToString("yyyy-MM-dd");
            await rateLimiter.WaitAsync(ct);
            var calResponse = await finnhub.GetEarningsCalendarAsync(from, to, symbol);

            var upcoming = calResponse.EarningsCalendar
                .Where(e => e.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                    && DateOnly.TryParse(e.Date, out _))
                .Select(e => DateOnly.Parse(e.Date))
                .Where(d => d >= DateOnly.FromDateTime(today))
                .OrderBy(d => d)
                .FirstOrDefault();

            if (upcoming != default)
            {
                var daysUntil = upcoming.DayNumber - DateOnly.FromDateTime(today).DayNumber;
                if (daysUntil <= gateDays)
                {
                    return new EarningsContext(
                        EarningsSetupType.UpcomingEarnings,
                        HasUpcomingEarnings: true,
                        DaysUntilEarnings: daysUntil,
                        HasRecentEarnings: false,
                        DaysSinceEarnings: null,
                        EpsSurprisePct: null,
                        BeatEstimate: false);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Earnings calendar fetch failed for {Symbol} — skipping earnings gate", symbol);
            return NoneContext;
        }

        // Step 2 — recent earnings check. History is fetched once here and carried on every
        // return path below so fundamental scoring can reuse it without a second Finnhub
        // call - the whole EarningsContext (including this list) is cached together.
        List<FinnhubEarningsEvent> history;
        try
        {
            await rateLimiter.WaitAsync(ct);
            history = await finnhub.GetEarningsHistoryAsync(symbol, 4);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Earnings history fetch failed for {Symbol} — defaulting to None", symbol);
            return NoneContext;
        }

        if (history.Count == 0) return NoneContext with { EarningsHistory = history };

        var most = history[0];
        if (!DateOnly.TryParse(most.Date, out var reportDate)) return NoneContext with { EarningsHistory = history };

        var daysSince = DateOnly.FromDateTime(today).DayNumber - reportDate.DayNumber;
        if (daysSince < 0 || daysSince > cfg.PostEarningsWindowDays)
            return NoneContext with { EarningsHistory = history };

        var surprise = most.SurprisePercent;
        EarningsSetupType setupType;
        bool beat = false;

        if (surprise > cfg.EpsSurpriseThresholdPct)
        {
            setupType = EarningsSetupType.PostEarningsBeat;
            beat = true;
        }
        else if (surprise < -cfg.EpsSurpriseThresholdPct)
        {
            setupType = EarningsSetupType.PostEarningsMiss;
        }
        else
        {
            setupType = EarningsSetupType.PostEarningsNeutral;
        }

        return new EarningsContext(
            setupType,
            HasUpcomingEarnings: false,
            DaysUntilEarnings: null,
            HasRecentEarnings: true,
            DaysSinceEarnings: daysSince,
            EpsSurprisePct: surprise,
            BeatEstimate: beat,
            EarningsHistory: history);
    }
}
