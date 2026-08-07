using SwingTrader.Agents.Trials;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

// The Trials page (transparency pivot, 6 Aug 2026): one registry of every
// mechanism that claims predictive power, with its pre-declared hypothesis,
// its live evidence count, and plain-language grading. All numbers are
// computed from data the mechanisms already write - this page measures, it
// never tracks.
public static class TrialsEndpoints
{
    private sealed record TrialCard(
        string Key, string Name, string Hypothesis, string DeclaredOn, string Status,
        int EvidenceN, int EvidenceTarget, string Grade, string GatesDecision, string? Note);

    public static RouteGroupBuilder MapTrialsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/trials", async (
            ITradeRepository trades,
            ISignalRepository signals,
            IAccountRepository accounts,
            IAccountRiskProfileRepository riskProfiles,
            IFilingEventRepository filingEvents,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            // All closed trades with real fills, whole account history.
            var closed = (await trades.GetTradeHistoryAsync(
                    ctx.AccountId, account.TradingMode, DateTime.UtcNow.AddYears(-2), DateTime.UtcNow))
                .Where(t => t.Status is not (TradeStatus.Open or TradeStatus.Pending or TradeStatus.Cancelled)
                    && t.ExitPrice is > 0 && t.EntryPrice > 0)
                .ToList();

            // Conviction lives on the originating signal.
            var convictionByTrade = new Dictionary<int, decimal?>();
            foreach (var t in closed.Where(t => t.SignalId.HasValue))
                convictionByTrade[t.Id] = (await signals.GetByIdAsync(ctx.AccountId, t.SignalId!.Value))?.ConvictionScore;

            var forwardBands = TrialsMath.ForwardScoreBands(closed);
            var convictionBands = TrialsMath.ConvictionBands(closed,
                t => convictionByTrade.TryGetValue(t.Id, out var c) ? c : null);
            var vetoSweep = TrialsMath.VetoFloorSweep(closed);
            var tilt = TrialsMath.SizingTilt(closed);

            // The ACTIVE book's dials (Default override or detected regime) -
            // trial statuses report the dial, evidence counts report the data.
            var activeBook = await riskProfiles.GetAsync(ctx.AccountId, ct);

            var events = await filingEvents.GetRecentAsync(90, ct);
            var scoredN = closed.Count(t => t.ForwardScoreAtEntry is not null);
            var stampedEvents = events.Count(e => e.ForwardStampedAt is not null);

            // The registry. Hypotheses verbatim from their specs, with the
            // date each was declared - a hypothesis written after seeing the
            // data is worthless, and this page makes that structural.
            var cards = new List<TrialCard>
            {
                new("forward-veto", "Forward-score veto (F3)",
                    "Gate-passing Buys with LOW Claude forward scores underperform; a floor improves outcomes.",
                    "2026-07-12", "Live (floor permissive)",
                    scoredN, 100, TrialsMath.Grade(scoredN, 100),
                    "Raise ForwardVetoFloor (Mark wants up to 7-9) ONLY on monotonic discrimination at n>=100.",
                    "Backtest conviction bands are GATE-score-only evidence and do not count against this trial."),
                new("sizing-tilt", "Forward-score size tilt (F2)",
                    "Sizing positions by forward score (up on strong, down on weak) beats equal sizing on the same trades.",
                    "2026-07-12",
                    // Status = the DIAL (the active book's aggressiveness),
                    // never inferred from evidence - a freshly-turned-on
                    // trial has a live dial and zero closed trades, and the
                    // card must say both truthfully.
                    activeBook.SizingAggressiveness > 0
                        ? $"Live (aggressiveness {activeBook.SizingAggressiveness:0.##})"
                        : "Live-inert (aggressiveness 0: trial not running)",
                    tilt.TiltedTrades, 60, TrialsMath.Grade(tilt.TiltedTrades, 60),
                    "Keep/raise SizingAggressiveness if tilt-weighted beats equal-weighted at n>=60 tilted trades.",
                    activeBook.SizingAggressiveness > 0 && tilt.TiltedTrades == 0
                        ? "Dial is ON but no tilted trades have CLOSED yet — evidence starts with the next exits."
                        : activeBook.SizingAggressiveness <= 0 ? "Turn the dial on for this trial to accumulate." : null),
                new("conviction-gate", "Technical conviction bands",
                    "Higher gate scores predict better outcomes (KNOWN COUNTER-SIGNAL: the 8+ band inverted in every backtest).",
                    "2026-07-15", "Live (drives Buy threshold)",
                    closed.Count, 100, TrialsMath.Grade(closed.Count, 100),
                    "A confirmed live inversion at n>=100 argues for the MaxConvictionForBuy ceiling.", null),
                new("regime-interim", "Interim regime book (H2 live-forward)",
                    "VolumeSpike restricted to Bear/Crisis adds value the calm-thin holdout could not referee.",
                    "2026-08-05", "Live-forward (explicitly interim, no OOS claim)",
                    0, 30, TrialsMath.Grade(0, 30),
                    "Keep/kill the Bear-book VS exclusion set on its first ~30 Bear-regime trades.",
                    "Counts only trades taken while the Bear/Crisis book governs — none yet (no Bear regime since 5 Aug)."),
                new("filing-events", "Small-cap filing events (P1 monitoring)",
                    "Routed 8-K events on genuinely small companies (public float < $250M) predict 20-day drift (bearish: negative; bullish agreements: positive).",
                    "2026-08-07", "Monitoring (Haiku, all 7 codes, nothing acts)",
                    stampedEvents, 50, TrialsMath.Grade(stampedEvents, 50),
                    "Bearish confirmation -> FD3-style veto overlay; bullish confirmation -> event long-book spec (P3).",
                    events.Count == 0
                        ? "Feed reset 7 Aug 2026: the first 41 events were captured under a size filter that let $16bn companies through, so they were discarded rather than pooled with evidence from the corrected population."
                        : $"{events.Count} events captured; forward stamping (P2) not built yet, so 0 are stamped — nothing here is evidence until it is."),
                new("insider-selling-veto", "Insider cluster-selling veto",
                    "Buys on symbols with detected insider cluster selling underperform; demoting them to Watch improves outcomes.",
                    "2026-08-06", "Live and ACTING",
                    0, 30, TrialsMath.Grade(0, 30),
                    "Keep/remove the hard demote depending on how demoted names perform vs taken trades.",
                    "Shipped as a hard gate on prior reasoning (insiders selling is a documented negative signal), NOT on measured evidence. Demoted-signal counterfactuals need pricing - same gap as FD3."),
                new("fd3-veto", "Filing-distress veto (FD3)",
                    "8-K distress codes on watchlist names justify entry vetoes and position exits.",
                    "2026-07-17", "Live and ACTING",
                    0, 30, TrialsMath.Grade(0, 30),
                    "None pending — but note: this mechanism has acted since July with NO outcome measurement wired.",
                    "TRANSPARENCY GAP: vetoed/exited counterfactuals are not priced anywhere. P2 stamping should cover FD3 flags too."),
            };

            return Results.Ok(new
            {
                GeneratedAt = DateTime.UtcNow,
                ClosedTrades = closed.Count,
                Cards = cards,
                ForwardBands = forwardBands,
                ConvictionBands = convictionBands,
                VetoSweep = vetoSweep,
                SizingTilt = tilt,
            });
        });

        return api;
    }
}
