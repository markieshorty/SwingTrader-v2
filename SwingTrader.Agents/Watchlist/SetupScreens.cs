using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Watchlist;

// One symbol's candidacy for one setup, with the score it ranks by INSIDE
// that setup's pool. Scores are never compared across setups - each pool is
// ranked and topped separately, which is the whole point of the union design
// and the reason no cross-factor normalisation is needed.
public sealed record SetupCandidacy(string Symbol, SetupType Setup, decimal Score);

// A symbol that survived the union, and every setup screen that surfaced it.
// The list order is the allocation order (see SetupScreens.Union).
public sealed record SetupUnionEntry(string Symbol, List<SetupType> Setups);

// Per-setup candidate screens (docs/screener-union-plan).
//
// The old screen ranked the whole universe by ONE factor - today's absolute
// percentage move - and handed the top 80 to Claude. That is a
// volatility-expansion filter, and it lines up with some detectors and not
// others:
//
//   VolumeSpike           trigger REQUIRES dayChange > 1.5%, a strict subset
//                         of the screen's own >= 1% rule - it cannot help but
//                         be fed
//   Breakout              needs band expansion on 1.5x volume, which produces
//                         a large move - also well fed
//   MomentumContinuation  needs only VolumeRatio > 1.0, no move at all
//   TrendFollowing        needs no volume and no move at all
//
// So the two trend-state setups can fire on a +0.3% day and were filtered out
// before Claude ever saw them. These screens surface candidates for each
// detector on its own terms.
//
// IMPORTANT: a screen only has to be a good APPROXIMATION of its detector.
// The real classification happens later in ResearchPipeline.DetectSetup with
// current candles; this decides who gets looked at, not what they are. That
// is why the thresholds below are deliberately looser than the detectors' -
// a near-miss today is often a trigger tomorrow, and a screen that exactly
// matched the detector would only ever surface names that already fired.
public static class SetupScreens
{
    // Detector thresholds, widened for candidate surfacing. Kept as named
    // constants so the relationship to DetectSetup stays visible.
    private const decimal OversoldRsiScreen = 40m;     // detector: < 35
    private const decimal BreakoutBandProximity = 0.98m; // within 2% of upper
    private const decimal MomentumRsiLow = 45m;        // detector: 50
    private const decimal MomentumRsiHigh = 70m;       // detector: 65
    private const decimal VolumeSpikeRatio = 1.5m;     // detector: > 2.0
    private const decimal TrendRsi = 50m;

    // Every setup this symbol is a plausible candidate for, with its rank
    // score in each. A symbol can appear in several pools - the union
    // de-dupes later, so being a good candidate twice is not double-counted.
    public static IEnumerable<SetupCandidacy> Evaluate(
        string symbol, IndicatorResult ind, decimal price, decimal? closeFourBarsAgo)
    {
        if (price <= 0) yield break;

        // ── OversoldRecovery: deeply oversold but already turning up. The
        // 4-bar recovery confirmation is the detector's guard against falling
        // knives, so the screen honours it when the history is there rather
        // than surfacing every faller.
        if (ind.Rsi14 is { } rsi && rsi < OversoldRsiScreen
            && ind.BollingerLower is { } lower && price > lower
            && (closeFourBarsAgo is null || price > closeFourBarsAgo))
        {
            // Deeper oversold ranks higher.
            yield return new SetupCandidacy(symbol, SetupType.OversoldRecovery, OversoldRsiScreen - rsi);
        }

        // ── Breakout: at or near the upper band with momentum behind it.
        if (ind.BollingerUpper is { } upper && upper > 0
            && price >= upper * BreakoutBandProximity
            && ind.MacdHistogram is > 0)
        {
            // Further through the band, on heavier volume, ranks higher.
            var extension = price / upper;
            yield return new SetupCandidacy(symbol, SetupType.Breakout,
                extension * Math.Max(ind.VolumeRatio ?? 1m, 0.1m));
        }

        // ── MomentumContinuation: mid-range RSI in a rising EMA structure.
        if (ind.Rsi14 is { } mrsi && mrsi >= MomentumRsiLow && mrsi <= MomentumRsiHigh
            && ind.Ema9 is { } e9 && ind.Ema21 is { } e21 && e9 > e21
            && ind.MacdHistogram is { } hist && hist > 0)
        {
            // MACD histogram as a fraction of price, so a $400 stock and a
            // $20 stock compete on the same terms.
            yield return new SetupCandidacy(symbol, SetupType.MomentumContinuation, hist / price);
        }

        // ── VolumeSpike: unusual participation. Ranked by volume ratio ONLY -
        // deliberately NOT by price move, which is what the old screen already
        // selected for and what coupled this setup to it.
        if (ind.VolumeRatio is { } vr && vr > VolumeSpikeRatio)
        {
            yield return new SetupCandidacy(symbol, SetupType.VolumeSpike, vr);
        }

        // ── TrendFollowing: the most starved setup under the old screen, since
        // its detector asks for no volume and no move whatsoever.
        if (ind.Ema9 is { } t9 && ind.Ema21 is { } t21 && t9 > t21
            && ind.Rsi14 is { } trsi && trsi > TrendRsi
            && ind.BollingerMid is { } mid && mid > 0 && price > mid)
        {
            // Wider EMA spread plus more room above the mid band = a cleaner,
            // better-established trend.
            yield return new SetupCandidacy(symbol, SetupType.TrendFollowing,
                (t9 - t21) / price + (price - mid) / mid);
        }
    }

    // Top N per setup, then the union. Ranking happens WITHIN each pool, so
    // scores never need to be normalised against each other - which is the
    // problem a single weighted composite would have had to solve.
    //
    // Returns symbol -> the setups that surfaced it, so per-setup outcomes
    // stay attributable. Without that attribution this cannot be judged
    // later and should not ship.
    public static List<SetupUnionEntry> Union(
        IEnumerable<SetupCandidacy> candidacies, int perSetup)
    {
        if (perSetup <= 0) return [];

        var pools = candidacies
            .GroupBy(c => c.Setup)
            .Select(g => g.OrderByDescending(c => c.Score).Take(perSetup).ToList())
            .ToList();

        // Round-robin: each setup's best, then each setup's second, and so on.
        // Downstream only MaxCandidatesForClaude survive the liquidity walk,
        // so ordering IS allocation - a flat "best score first" list would be
        // meaningless across pools whose scores are on different scales, and
        // ordering by price move would rebuild the very bias this replaces.
        var order = new List<string>();
        var setupsBySymbol = new Dictionary<string, List<SetupType>>(StringComparer.OrdinalIgnoreCase);
        var depth = pools.Count == 0 ? 0 : pools.Max(p => p.Count);

        for (var rank = 0; rank < depth; rank++)
            foreach (var pool in pools)
            {
                if (rank >= pool.Count) continue;
                var pick = pool[rank];
                if (!setupsBySymbol.TryGetValue(pick.Symbol, out var setups))
                {
                    setupsBySymbol[pick.Symbol] = setups = [];
                    order.Add(pick.Symbol);
                }
                if (!setups.Contains(pick.Setup)) setups.Add(pick.Setup);
            }

        return order.Select(s => new SetupUnionEntry(s, setupsBySymbol[s])).ToList();
    }
}
