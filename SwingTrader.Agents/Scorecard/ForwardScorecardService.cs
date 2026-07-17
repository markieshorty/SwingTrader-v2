using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Scorecard;

// ── Result shapes (serialized straight to the Intelligence Scorecard tab) ────

// One forward-score band of closed trades: did a higher at-entry Forward score
// actually mean better outcomes? If not, the sizing tilt and veto are theatre.
public sealed record ForwardBucket(string Band, int Trades, decimal? WinRate, decimal? AvgReturnPct);

// One blocked Buy (a gate-passing signal demoted to Watch) with its
// counterfactual: what taking the entry anyway would have returned under the
// standard exit rules. Negative = the block saved money.
public sealed record BlockedBuyRow(
    DateOnly SignalDate, string Symbol, string Source, decimal? ForwardScore,
    decimal? CounterfactualReturnPct, string? ExitReason, int? TradingDaysHeld, bool StillOpen);

public sealed record BlockedBuySummary(
    string Source, int Blocked, int Replayed, decimal AvgReturnPct, decimal TotalReturnPct, int WouldHaveWon);

// A shadow/forward signal's predictive read: the score at signal time vs the
// symbol's return over the following 10 trading days.
public sealed record SignalCorrelation(
    string Signal, int Pairs, decimal? PearsonR,
    decimal? AvgFwdReturnWhenNegative, decimal? AvgFwdReturnWhenPositive);

public sealed record ForwardScorecardResult(
    int WindowDays,
    List<ForwardBucket> ForwardBuckets,
    List<BlockedBuySummary> BlockedSummaries,
    List<BlockedBuyRow> BlockedBuys,
    List<SignalCorrelation> Correlations);

public interface IForwardScorecardService
{
    Task<ForwardScorecardResult> BuildAsync(int accountId, int windowDays, CancellationToken ct = default);
}

// The forward-side feedback loop (the gate has Refinement; this is the same
// discipline for everything the gate doesn't cover): forward-score buckets on
// closed trades, counterfactual replays of every blocked Buy (forward veto,
// distress veto, setup-disabled), and score-vs-forward-return correlations for
// the filing and second-hop signals. Read-only analysis over data the pipeline
// already persists - no Claude, no new collection.
public class ForwardScorecardService(
    ITradeRepository trades,
    ISignalRepository signals,
    IAccountRepository accounts,
    ISetupTacticsRepository setupTactics,
    IHistoricalCandleRepository candles,
    ILogger<ForwardScorecardService> logger) : IForwardScorecardService
{
    private const int ForwardReturnTradingDays = 10;

    public async Task<ForwardScorecardResult> BuildAsync(int accountId, int windowDays, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-windowDays));

        // ── Section A: forward-score buckets on CLOSED trades ────────────────
        var history = (await trades.GetTradeHistoryAsync(
                accountId, account.TradingMode, since.ToDateTime(TimeOnly.MinValue), DateTime.UtcNow))
            .Where(t => t.Status is not (TradeStatus.Open or TradeStatus.Pending or TradeStatus.Cancelled)
                && t.ExitPrice is > 0 && t.EntryPrice > 0)
            .ToList();

        decimal ReturnPct(Trade t) => Math.Round((t.ExitPrice!.Value - t.EntryPrice) / t.EntryPrice * 100m, 2);

        ForwardBucket Bucket(string band, Func<Trade, bool> match)
        {
            var set = history.Where(match).ToList();
            return new ForwardBucket(band, set.Count,
                set.Count > 0 ? Math.Round((decimal)set.Count(t => ReturnPct(t) > 0) / set.Count, 4) : null,
                set.Count > 0 ? Math.Round(set.Average(ReturnPct), 2) : null);
        }

        var buckets = new List<ForwardBucket>
        {
            Bucket("Forward < 4", t => t.ForwardScoreAtEntry is < 4m),
            Bucket("Forward 4–6", t => t.ForwardScoreAtEntry is >= 4m and < 6m),
            Bucket("Forward ≥ 6", t => t.ForwardScoreAtEntry is >= 6m),
            Bucket("No forward score", t => t.ForwardScoreAtEntry is null),
        };

        // ── Section B: blocked Buys + counterfactuals ────────────────────────
        // A blocked Buy = the gate passed but the recommendation ended Watch.
        // Attribution mirrors the pipeline's demotion order: the WouldBeVetoed
        // flag marks the forward veto; the distress veto only writes reasoning
        // text; anything else demoted with a passing gate is the per-setup
        // live switch.
        var windowSignals = (await signals.GetSinceDateAsync(accountId, since)).ToList();
        var blocked = windowSignals
            .Where(s => s.WouldPassGate && s.Recommendation == Recommendation.Watch)
            .ToList();

        static string SourceOf(StockSignal s) =>
            s.Reasoning?.Contains("Distress veto", StringComparison.OrdinalIgnoreCase) == true ? "Distress veto"
            : s.WouldBeVetoed ? "Forward veto"
            : "Setup disabled";

        // One targeted candle read covers the replays AND the correlations.
        var correlationSignals = windowSignals
            .Where(s => s.FilingDeltaScore is not null || s.SecondHopScore is not null)
            .ToList();
        var symbolsNeeded = blocked.Select(s => s.Symbol)
            .Concat(correlationSignals.Select(s => s.Symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var barsBySymbol = symbolsNeeded.Count > 0
            ? await candles.GetForSymbolsAsync(symbolsNeeded, since, ct)
            : new Dictionary<string, List<HistoricalCandle>>(StringComparer.OrdinalIgnoreCase);

        // CURRENT per-setup tactics shape the replays. Tactics aren't
        // snapshotted onto signals, so a tactics edit since the signal shifts
        // the counterfactual slightly - accepted and documented (the question
        // is directional: did blocking the entry help?).
        var tactics = (await setupTactics.GetAllAsync(accountId, ct))
            .ToDictionary(t => t.SetupType, t => t);

        var rows = new List<BlockedBuyRow>();
        foreach (var s in blocked.OrderByDescending(s => s.SignalDate))
        {
            CounterfactualReplay.Outcome? outcome = null;
            if (barsBySymbol.TryGetValue(s.Symbol, out var bars) && tactics.TryGetValue(s.SetupType, out var tac))
            {
                outcome = CounterfactualReplay.Run(
                    bars, s.SignalDate,
                    tac.StopLossPct, tac.TargetPct, tac.GuideHoldDays,
                    (decimal)tac.TrailingActivationPct, (decimal)tac.TrailingDistancePct);
            }
            rows.Add(new BlockedBuyRow(
                s.SignalDate, s.Symbol, SourceOf(s), s.ForwardScore,
                outcome?.ReturnPct, outcome?.ExitReason, outcome?.TradingDaysHeld, outcome?.StillOpen ?? false));
        }

        var summaries = rows
            .GroupBy(r => r.Source)
            .Select(g =>
            {
                var replayed = g.Where(r => r.CounterfactualReturnPct is not null).ToList();
                return new BlockedBuySummary(
                    g.Key, g.Count(), replayed.Count,
                    replayed.Count > 0 ? Math.Round(replayed.Average(r => r.CounterfactualReturnPct!.Value), 2) : 0m,
                    Math.Round(replayed.Sum(r => r.CounterfactualReturnPct ?? 0m), 2),
                    replayed.Count(r => r.CounterfactualReturnPct > 0));
            })
            .OrderByDescending(s => s.Blocked)
            .ToList();

        // ── Section C: shadow/forward signal correlations ────────────────────
        var correlations = new List<SignalCorrelation>
        {
            Correlate("Filing delta", correlationSignals, s => s.FilingDeltaScore, barsBySymbol),
            Correlate("Second-hop", correlationSignals, s => s.SecondHopScore, barsBySymbol),
        };

        logger.LogInformation(
            "Scorecard for account {AccountId} ({Days}d): {Trades} closed trades, {Blocked} blocked Buys ({Replayed} replayed), {Pairs} correlation pairs",
            accountId, windowDays, history.Count, rows.Count, rows.Count(r => r.CounterfactualReturnPct is not null),
            correlations.Sum(c => c.Pairs));

        return new ForwardScorecardResult(windowDays, buckets, summaries, rows, correlations);
    }

    // Score at signal time vs the symbol's close-to-close return over the next
    // N trading days - the simplest honest "did this signal point the right
    // way" read. Pairs lacking enough forward bars are skipped.
    private static SignalCorrelation Correlate(
        string name, List<StockSignal> signals, Func<StockSignal, decimal?> score,
        Dictionary<string, List<HistoricalCandle>> barsBySymbol)
    {
        var pairs = new List<(decimal Score, decimal FwdReturn)>();
        foreach (var s in signals)
        {
            if (score(s) is not { } v) continue;
            if (!barsBySymbol.TryGetValue(s.Symbol, out var bars)) continue;
            var idx = bars.FindIndex(b => b.Date >= s.SignalDate);
            if (idx < 0 || idx + ForwardReturnTradingDays >= bars.Count) continue;
            var start = bars[idx].Close;
            if (start <= 0) continue;
            var end = bars[idx + ForwardReturnTradingDays].Close;
            pairs.Add((v, (end - start) / start * 100m));
        }

        var negatives = pairs.Where(p => p.Score < 0).ToList();
        var positives = pairs.Where(p => p.Score > 0).ToList();
        return new SignalCorrelation(
            name, pairs.Count,
            Pearson(pairs),
            negatives.Count > 0 ? Math.Round(negatives.Average(p => p.FwdReturn), 2) : null,
            positives.Count > 0 ? Math.Round(positives.Average(p => p.FwdReturn), 2) : null);
    }

    internal static decimal? Pearson(IReadOnlyList<(decimal X, decimal Y)> pairs)
    {
        if (pairs.Count < 3) return null; // meaningless below a handful of points
        var mx = pairs.Average(p => p.X);
        var my = pairs.Average(p => p.Y);
        decimal cov = 0, vx = 0, vy = 0;
        foreach (var (x, y) in pairs)
        {
            cov += (x - mx) * (y - my);
            vx += (x - mx) * (x - mx);
            vy += (y - my) * (y - my);
        }
        if (vx == 0 || vy == 0) return null; // constant series - r undefined
        var r = (double)cov / (Math.Sqrt((double)vx) * Math.Sqrt((double)vy));
        return Math.Round((decimal)r, 3);
    }
}
