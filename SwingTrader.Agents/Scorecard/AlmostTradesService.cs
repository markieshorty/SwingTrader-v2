using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Scorecard;

// One signal that was GOOD ENOUGH TO BUY and wasn't bought, with what taking
// it would have returned.
public sealed record AlmostTrade(
    DateOnly SignalDate, string Symbol, string? CompanyName,
    decimal? GateScore, decimal? ForwardScore, string Setup,
    decimal? CounterfactualReturnPct, string? ExitReason,
    int? TradingDaysHeld, bool StillOpen);

public sealed record AlmostTradesResult(
    int WindowDays,
    int Qualified,            // cleared the gate AND the forward floor
    int Replayed,             // of those, how many had bars to replay
    decimal AvgReturnPct,     // mean counterfactual return of the replayed
    int WouldHaveWon,
    decimal ForwardFloor,     // the bar in force, for context
    List<AlmostTrade> Trades);

// "Almost trades" (docs/selective-buy-plan): signals that passed the gate AND
// cleared the forward floor - i.e. the system WANTED to buy them - but were
// not taken because the single position was already occupied.
//
// This exists because the selective design creates the problem it measures.
// With MaxOpenPositions = 1 the book is full roughly three-quarters of the
// time, so most qualifying signals are turned away. Without replaying them
// there is no way to tell whether the one that got bought was the right one,
// and H-SEL1 would accumulate evidence at a twelfth of the available rate.
//
// Deliberately SEPARATE from the forward scorecard's blocked-Buy panel. That
// one answers "was blocking this entry right?" about vetoes - judgements
// ABOUT a symbol. A slot skip is not a judgement; it says only that capacity
// ran out, so pooling the two would measure our own throughput and report it
// as discrimination. Same replay engine, different question.
public class AlmostTradesService(
    ISignalRepository signals,
    IHistoricalCandleRepository candles,
    IAccountRiskProfileRepository riskProfiles,
    ISetupTacticsRepository setupTactics) : IAlmostTradesService
{
    public async Task<AlmostTradesResult> BuildAsync(int accountId, int windowDays, CancellationToken ct = default)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-windowDays));
        var profile = await riskProfiles.GetAsync(accountId, ct);

        // A Buy that execution never took - whatever stopped it: no free slot,
        // insufficient cash, a rejected order. WasExecuted is the claim flag
        // execution sets when it commits to a signal (and clears if the order
        // is cancelled), so its absence is exactly "wanted, not taken".
        //
        // Until 11 Aug 2026 this looked for BlockReason.PortfolioFull, because
        // a full book demoted Buys to Watch at scoring time. That demotion is
        // gone - it discarded qualifying signals over a timing accident - so
        // the population is now defined by outcome rather than by a reason
        // recorded hours too early.
        //
        // TODAY is excluded: execution runs after research, so today's Buys are
        // legitimately unexecuted for a few hours and would read as misses.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var qualified = (await signals.GetSinceDateAsync(accountId, since))
            .Where(s => s.Recommendation == Recommendation.Buy
                && !s.WasExecuted
                && s.SignalDate < today)
            .OrderByDescending(s => s.SignalDate)
            .ToList();

        if (qualified.Count == 0)
            return new AlmostTradesResult(windowDays, 0, 0, 0m, 0, profile.ForwardBuyThreshold, []);

        var barsBySymbol = await candles.GetForSymbolsAsync(
            qualified.Select(s => s.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), since, ct);

        // Current per-setup tactics shape the replay, matching the forward
        // scorecard's convention - a tactics edit since the signal shifts the
        // counterfactual slightly, which is accepted: the question is
        // directional, not a precise fill.
        var tactics = (await setupTactics.GetAllAsync(accountId, ct))
            .ToDictionary(t => t.SetupType, t => t);

        var rows = new List<AlmostTrade>(qualified.Count);
        foreach (var s in qualified)
        {
            ct.ThrowIfCancellationRequested();

            var tac = tactics.TryGetValue(s.SetupType, out var found) ? found : null;
            var outcome = barsBySymbol.TryGetValue(s.Symbol, out var bars) && bars.Count > 0
                ? CounterfactualReplay.Run(
                    bars, s.SignalDate,
                    tac?.StopLossPct ?? profile.StopLossPct,
                    tac?.TargetPct ?? profile.TargetPct,
                    tac?.GuideHoldDays ?? profile.MaxHoldDays,
                    (decimal)(tac?.TrailingActivationPct ?? profile.TrailingActivationPct),
                    (decimal)(tac?.TrailingDistancePct ?? profile.TrailingDistancePct))
                : null;

            rows.Add(new AlmostTrade(
                s.SignalDate, s.Symbol, s.CompanyName, s.GateScore, s.ForwardScore,
                s.SetupType.ToString(), outcome?.ReturnPct, outcome?.ExitReason,
                outcome?.TradingDaysHeld, outcome?.StillOpen ?? false));
        }

        var replayed = rows.Where(r => r.CounterfactualReturnPct is not null).ToList();
        return new AlmostTradesResult(
            windowDays,
            qualified.Count,
            replayed.Count,
            replayed.Count > 0 ? Math.Round(replayed.Average(r => r.CounterfactualReturnPct!.Value), 2) : 0m,
            replayed.Count(r => r.CounterfactualReturnPct > 0),
            profile.ForwardBuyThreshold,
            rows);
    }
}
