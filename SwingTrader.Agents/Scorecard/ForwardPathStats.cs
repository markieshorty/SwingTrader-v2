using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Scorecard;

// What the price did after a signal, IGNORING stops, targets and trails
// entirely (docs/scoring-engine-plan SPEC §3).
//
// Deliberately separate from CounterfactualReplay. That answers "what would this
// trade have returned under these dials"; this answers "what did the price
// actually do", which is a property of the bars alone. The per-setup calibration
// targets P(+25% in 40 days) - a rule-free quantity - so it must be measured
// here. Deriving it from replayed returns would be wrong twice: a 25% target
// caps the very winners it is meant to count, and every dial sweep would move
// the calibration set underneath the thing being calibrated.
//
// Gross of costs, by design. These are path facts, not tradeable results; the
// 0.25%/side round trip belongs on CounterfactualReplay's output.
public static class ForwardPathStats
{
    public const int Horizon = 40;
    public const decimal RightTailThresholdPct = 25m;

    public sealed record Stats(
        DateOnly EntryDate,
        decimal EntryPrice,
        decimal? Fwd5Pct,
        decimal? Fwd20Pct,
        decimal? Fwd40Pct,
        decimal? MaxFavorablePct,
        decimal? MaxAdversePct,
        bool? HitPlus25Within40,
        bool? HitMinus25Within40);

    // bars: this symbol's daily bars ordered by date, covering the signal date
    // onward. Null when no entry was possible (no bar after the signal, or a
    // zero/negative open) - the same entry convention as CounterfactualReplay,
    // so the two are directly comparable on the same signal.
    public static Stats? Compute(IReadOnlyList<HistoricalCandle> bars, DateOnly signalDate)
    {
        var entryIdx = -1;
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Date > signalDate) { entryIdx = i; break; }
        }
        if (entryIdx < 0) return null;

        var entry = bars[entryIdx].Open;
        if (entry <= 0) return null;

        // Bars available after entry. A window that runs past the end of the
        // data is reported as null rather than truncated: a partial window
        // understates both tails, and silently biases every statistic built on
        // it toward the middle.
        var available = bars.Count - 1 - entryIdx;

        decimal? FwdAt(int k) =>
            available >= k ? Pct(bars[entryIdx + k].Close, entry) : null;

        decimal? maxFav = null, maxAdv = null;
        if (available >= Horizon)
        {
            // Intraday extremes, not closes: a target order fills when the high
            // touches it, so "did this ever reach +25%" is a high/low question.
            var hi = bars[entryIdx].High;
            var lo = bars[entryIdx].Low;
            for (var i = entryIdx + 1; i <= entryIdx + Horizon; i++)
            {
                if (bars[i].High > hi) hi = bars[i].High;
                if (bars[i].Low < lo) lo = bars[i].Low;
            }
            maxFav = Pct(hi, entry);
            maxAdv = Pct(lo, entry);
        }

        return new Stats(
            bars[entryIdx].Date, entry,
            FwdAt(5), FwdAt(20), FwdAt(Horizon),
            maxFav, maxAdv,
            maxFav is { } f ? f >= RightTailThresholdPct : null,
            maxAdv is { } a ? a <= -RightTailThresholdPct : null);
    }

    private static decimal Pct(decimal value, decimal entry) =>
        Math.Round((value - entry) / entry * 100m, 4);
}
