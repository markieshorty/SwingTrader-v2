using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Research;

// Graded setup detection (docs/scoring-engine-plan SPEC P1).
//
// Three things change from SetupDetector:
//
//   1. MEMBERSHIP IS GRADED, 0-1. The legacy detector was boolean, so RSI 34.9
//      and RSI 25 were the same "OversoldRecovery", and a name 0.1% over its
//      upper band was the same "Breakout" as one 4% over. How strongly a setup
//      qualifies is the most informative thing about it, and it was discarded.
//
//   2. NO FIRST-MATCH-WINS. The legacy detector returned one setup by list
//      order, so a name qualifying as both Breakout and MomentumContinuation got
//      Breakout because it was listed earlier - an ordering nobody validated.
//      Every setup is now evaluated and a name may belong to several.
//
//   3. TRENDFOLLOWING IS GONE as a setup. It had no trigger, so it re-fired
//      daily on the same ongoing fact (SNOW: 23 of 29 trading days; 3.9 signals
//      per symbol against VolumeSpike's 1.0). Trend is returned as CONTEXT
//      instead, available to every setup rather than competing as an entry.
//
// Membership is the MINIMUM of a setup's condition scores - a setup is only as
// strong as its weakest leg. Using a product would let five decent conditions
// score worse than two perfect ones; using a mean would let one badly-failed
// condition hide behind four good ones, which is exactly the falling-knife case.
public static class GradedSetupDetector
{
    public sealed record Result(
        IReadOnlyDictionary<SetupType, decimal> Memberships,
        decimal TrendStrength)
    {
        public (SetupType Setup, decimal Membership) Best() =>
            Memberships.Count == 0
                ? (SetupType.Unknown, 0m)
                : Memberships.Aggregate((a, b) => a.Value >= b.Value ? a : b) is var kv
                    ? (kv.Key, kv.Value) : (SetupType.Unknown, 0m);
    }

    public static Result Detect(
        IndicatorResult ind, IReadOnlyList<StockCandle> candles, SetupDialsV2 dials)
    {
        var memberships = new Dictionary<SetupType, decimal>();
        if (candles.Count == 0) return new Result(memberships, 0m);

        var price = candles[^1].Close;
        if (price <= 0) return new Result(memberships, 0m);

        void Add(SetupType setup, decimal m)
        {
            if (m >= dials.MinMembership) memberships[setup] = Math.Round(m, 4);
        }

        Add(SetupType.OversoldRecovery, Oversold(ind, candles, price, dials));
        Add(SetupType.Breakout, Breakout(ind, candles, price, dials));
        Add(SetupType.MomentumContinuation, Momentum(ind, dials));
        Add(SetupType.VolumeSpike, VolumeSpike(ind, candles, price, dials));

        return new Result(memberships, TrendStrength(ind, price));
    }

    // ── Setups ───────────────────────────────────────────────────────────────

    private static decimal Oversold(
        IndicatorResult ind, IReadOnlyList<StockCandle> candles, decimal price, SetupDialsV2 d)
    {
        if (ind.Rsi14 is not { } rsi || ind.BollingerLower is not { } lower) return 0m;

        // Peaks at the ceiling, fades toward the floor. Below the floor this is
        // a falling knife, not a dip, and membership goes to zero rather than
        // continuing to rise as the legacy scorer's shape implied.
        var depth = rsi > d.OversoldRsiCeiling ? 0m
            : rsi <= d.OversoldRsiFloor ? 0m
            : Ramp(rsi, d.OversoldRsiFloor, d.OversoldRsiCeiling) is var t
                ? Math.Min(1m, t * 2m) // full credit once clear of the floor
                : 0m;
        if (depth <= 0m) return 0m;

        // Price reclaimed the lower band, by the configured margin.
        var reclaim = lower <= 0 ? 0m
            : Ramp((price - lower) / lower, d.OversoldBandReclaimPct, d.OversoldBandReclaimPct + 0.03m);

        // The falling-knife guard. recoveryBars = 0 disables it entirely, which
        // is the retired "loose" variant expressed as a dial.
        var recovery = 1m;
        if (d.OversoldRecoveryBars > 0)
        {
            if (candles.Count <= d.OversoldRecoveryBars) return 0m;
            var back = candles[^(d.OversoldRecoveryBars + 1)].Close;
            if (back <= 0) return 0m;
            var move = (price - back) / back;
            recovery = Ramp(move, d.OversoldRecoveryMinPct, d.OversoldRecoveryMinPct + 0.04m);
        }

        return Min3(depth, reclaim, recovery);
    }

    private static decimal Breakout(
        IndicatorResult ind, IReadOnlyList<StockCandle> candles, decimal price, SetupDialsV2 d)
    {
        if (ind.BollingerUpper is not { } upper || upper <= 0) return 0m;
        if (ind.MacdHistogram is not { } macd || macd <= 0) return 0m;

        var extension = Ramp((price - upper) / upper, d.BreakoutBandDistancePct,
            d.BreakoutBandDistancePct + 0.03m);
        if (extension <= 0m) return 0m;

        var volume = ind.VolumeRatio is not { } vr ? 0m
            : Ramp(vr, d.BreakoutVolumeFloor, d.BreakoutVolumeIdeal);

        // What did it break out OF? A tight prior range is a coil; a wide one is
        // just noise that happened to poke through a band.
        var tightness = Tightness(candles, d.BreakoutPriorRangeBars);
        var quality = tightness is not { } tv ? 0.5m // unknowable - stay neutral
            : 1m - Ramp(tv, d.BreakoutTightIdeal, d.BreakoutTightWorst);

        return Min3(extension, volume, quality);
    }

    private static decimal Momentum(IndicatorResult ind, SetupDialsV2 d)
    {
        if (ind.Rsi14 is not { } rsi) return 0m;
        if (ind.Ema9 is not { } fast || ind.Ema21 is not { } slow || fast <= slow) return 0m;
        if (ind.MacdHistogram is not { } macd || macd <= 0) return 0m;

        // Inside the band is good; the edges taper rather than cliff.
        if (rsi < d.MomentumRsiFloor || rsi > d.MomentumRsiCeiling) return 0m;
        var mid = (d.MomentumRsiFloor + d.MomentumRsiCeiling) / 2m;
        var half = (d.MomentumRsiCeiling - d.MomentumRsiFloor) / 2m;
        var band = half <= 0 ? 0m : 1m - Math.Abs(rsi - mid) / half;

        var spread = slow <= 0 ? 0m : Ramp((fast - slow) / slow, 0m, 0.05m);
        var volume = ind.VolumeRatio is not { } vr ? 0m : Ramp(vr, d.MomentumVolumeFloor, 2.0m);

        return Min3(band, spread, volume);
    }

    private static decimal VolumeSpike(
        IndicatorResult ind, IReadOnlyList<StockCandle> candles, decimal price, SetupDialsV2 d)
    {
        if (ind.VolumeRatio is not { } vr) return 0m;
        if (candles.Count < 2) return 0m;

        var prev = candles[^2].Close;
        if (prev <= 0) return 0m;

        var volume = Ramp(vr, d.SpikeVolumeFloor, d.SpikeVolumeIdeal);
        if (volume <= 0m) return 0m;

        var change = Ramp((price - prev) / prev, d.SpikeChangeFloorPct, d.SpikeChangeFloorPct + 0.03m);
        return Math.Min(volume, change);
    }

    // ── Context, not a setup ─────────────────────────────────────────────────

    // The former TrendFollowing. A dip inside an uptrend is a different
    // proposition from the same dip inside a downtrend, so this belongs to every
    // setup rather than competing with them.
    private static decimal TrendStrength(IndicatorResult ind, decimal price)
    {
        if (ind.Ema9 is not { } fast || ind.Ema21 is not { } slow || slow <= 0) return 0.5m;
        // 0.5 is neutral; above means uptrend, below means downtrend.
        var spread = (fast - slow) / slow;
        return Math.Clamp(0.5m + spread * 10m, 0m, 1m);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Prior-window range as a fraction of its mean close. Null when there are
    // not enough bars to judge.
    private static decimal? Tightness(IReadOnlyList<StockCandle> candles, int bars)
    {
        if (bars <= 1 || candles.Count < bars + 1) return null;

        // Excludes the breakout bar itself: including it would measure the
        // breakout rather than the coil it came from.
        var window = candles.Skip(candles.Count - bars - 1).Take(bars).ToList();
        var hi = window.Max(c => c.High);
        var lo = window.Min(c => c.Low);
        var mean = window.Average(c => c.Close);
        return mean <= 0 ? null : (hi - lo) / mean;
    }

    // 0 at `worst`, 1 at `ideal`, linear between - in either direction, so the
    // caller does not have to care which end is better.
    private static decimal Ramp(decimal value, decimal zeroAt, decimal oneAt)
    {
        if (zeroAt == oneAt) return value >= oneAt ? 1m : 0m;
        var t = (value - zeroAt) / (oneAt - zeroAt);
        return Math.Clamp(t, 0m, 1m);
    }

    private static decimal Min3(decimal a, decimal b, decimal c) => Math.Min(a, Math.Min(b, c));
}
