using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Trading;

// Dynamic take-profit targets (1 Aug 2026): a flat 25% target is decoration
// on a stock whose biggest recent move is 8% - fewer than 10% of backtest
// trades ever exited at Target. Instead of a single figure, the target can be
// derived from the stock's own behaviour and clamped to a [floor, ceiling]
// band. Pure and shared by live (Research/Report/Execution) and the
// backtester so both derive identical levels.
public static class DynamicTarget
{
    // Returns the EFFECTIVE target fraction (0.10 = +10%). Missing inputs
    // always fall back to the flat target - a data gap must never produce a
    // degenerate level. The band only applies to DERIVED targets; Flat mode
    // returns the configured value untouched.
    public static decimal ResolvePct(
        TargetMode mode,
        decimal flatTargetPct,
        decimal? atr,
        decimal price,
        decimal? nearestResistance,
        decimal atrTargetMultiple,
        decimal bandFloorPct,
        decimal bandCeilingPct)
    {
        if (mode == TargetMode.Flat || price <= 0 || bandFloorPct <= 0 || bandCeilingPct <= bandFloorPct)
            return flatTargetPct;

        decimal derived;
        switch (mode)
        {
            case TargetMode.AtrScaled:
                // "m normal days of favourable movement" scaled to THIS stock.
                if (atr is not { } a || a <= 0) return flatTargetPct;
                derived = atrTargetMultiple * a / price;
                break;

            case TargetMode.ResistanceCapped:
                // Mean-reversion bounces stall at resistance: cap the flat
                // target just under it (0.5% shy so the order isn't asking for
                // a level the chart says is a wall). Resistance at/below the
                // entry price is stale data - keep the flat target.
                if (nearestResistance is not { } res || res <= price) return flatTargetPct;
                derived = Math.Min(flatTargetPct, (res * 0.995m - price) / price);
                break;

            default:
                return flatTargetPct;
        }

        return Math.Clamp(derived, bandFloorPct, bandCeilingPct);
    }
}
