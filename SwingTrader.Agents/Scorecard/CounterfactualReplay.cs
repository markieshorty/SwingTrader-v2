using SwingTrader.Core.Constants;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Scorecard;

// What WOULD have happened if a blocked Buy had been taken: entry at the next
// trading day's open, then the same gap-aware stop/target/trailing/time-cap
// walk the historic backtester runs (probation deliberately NOT simulated -
// the counterfactual answers "was blocking the ENTRY right?", not "how well
// would the health check have managed it?"). Round-trip costs match the
// backtester's 0.25%/side so the numbers are comparable with Lab results.
// Pure static so the walk is directly unit-testable.
public static class CounterfactualReplay
{
    public const decimal CostPerSide = 0.0025m;

    public sealed record Outcome(
        decimal ReturnPct,        // net of round-trip costs
        string ExitReason,        // StopLoss / Target / Trailing / TimeExit / StillOpen
        int TradingDaysHeld,
        DateOnly EntryDate,
        DateOnly? ExitDate,       // null while StillOpen
        bool StillOpen);          // ran out of bars - ReturnPct is mark-to-last-close

    // bars: this symbol's daily bars ordered by date (any range that covers the
    // signal date onward). Null when no entry was possible (no bar after the
    // signal date, or a zero/negative open).
    public static Outcome? Run(
        IReadOnlyList<HistoricalCandle> bars, DateOnly signalDate,
        decimal stopLossPct, decimal targetPct, int guideHoldDays,
        decimal trailingActivationPct, decimal trailingDistancePct)
    {
        // Entry: the first bar strictly after the signal date (live executes
        // the morning's signals from 9:20 ET the same day; the signal is
        // scored pre-market, so "next bar" IS the signal day's bar when the
        // bars include it - and the day after for late signals).
        var entryIdx = -1;
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Date > signalDate) { entryIdx = i; break; }
        }
        if (entryIdx < 0) return null;

        var entry = bars[entryIdx].Open;
        if (entry <= 0) return null;

        var stop = entry * (1 - stopLossPct);
        var target = entry * (1 + targetPct);
        var hardCeiling = (int)Math.Ceiling(guideHoldDays * CapitalRules.HoldCeilingMultiple);
        decimal? trailingStop = null;

        for (var i = entryIdx; i < bars.Count; i++)
        {
            var bar = bars[i];
            var daysHeld = i - entryIdx;

            // Same priority order as HistoricBacktester.CheckExit: gap-aware
            // stop first, then target, then trailing, then the hard time cap.
            if (bar.Open <= stop) return Close(bar.Open, "StopLoss", entry, daysHeld, bars[entryIdx].Date, bar.Date);
            if (bar.Low <= stop) return Close(stop, "StopLoss", entry, daysHeld, bars[entryIdx].Date, bar.Date);
            if (bar.Open >= target) return Close(bar.Open, "Target", entry, daysHeld, bars[entryIdx].Date, bar.Date);
            if (bar.High >= target) return Close(target, "Target", entry, daysHeld, bars[entryIdx].Date, bar.Date);
            if (trailingStop is { } trail)
            {
                if (bar.Open <= trail) return Close(bar.Open, "Trailing", entry, daysHeld, bars[entryIdx].Date, bar.Date);
                if (bar.Low <= trail) return Close(trail, "Trailing", entry, daysHeld, bars[entryIdx].Date, bar.Date);
            }
            if (daysHeld > hardCeiling) return Close(bar.Close, "TimeExit", entry, daysHeld, bars[entryIdx].Date, bar.Date);

            // Trailing arms/ratchets at the close, like the backtester.
            if (bar.Close >= entry * (1 + trailingActivationPct))
            {
                var newTrail = bar.Close * (1 - trailingDistancePct);
                if (trailingStop is null || newTrail > trailingStop) trailingStop = newTrail;
            }
        }

        // Ran out of bars - still open, marked to the last close.
        var last = bars[^1];
        var open_ = Close(last.Close, "StillOpen", entry, bars.Count - 1 - entryIdx, bars[entryIdx].Date, null);
        return open_ with { StillOpen = true };
    }

    private static Outcome Close(decimal exitPrice, string reason, decimal entry, int daysHeld, DateOnly entryDate, DateOnly? exitDate)
    {
        var proceeds = exitPrice * (1 - CostPerSide);
        var cost = entry * (1 + CostPerSide);
        return new Outcome(Math.Round((proceeds - cost) / cost * 100m, 2), reason, daysHeld, entryDate, exitDate, StillOpen: false);
    }
}
