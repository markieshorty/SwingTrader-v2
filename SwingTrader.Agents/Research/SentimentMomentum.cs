namespace SwingTrader.Agents.Research;

// Sentiment MOMENTUM: today's Claude-scored sentiment level, tilted by how it
// compares to the symbol's own recent history from the sentiment archive.
// The level alone is a ~3-day snapshot; the direction of travel (a stock's
// news turning positive after a flat/negative stretch) is the leading part
// of the signal and costs nothing - the SentimentDailyScore archive accrues
// daily anyway. Pure and deterministic given its inputs so it's trivially
// testable and the Refinement agent's correlations stay interpretable.
public static class SentimentMomentum
{
    public sealed record Result(decimal BlendedScore, decimal? Delta, int HistoryCount);

    // level: today's raw sentiment in [-1, 1].
    // priorScores: the symbol's recent archived daily scores (today excluded).
    // momentumWeight: how much of the (clamped) delta is ADDED to the level -
    //   additive rather than averaged, so thin history never dilutes a strong
    //   level toward neutral; no history at all means the level passes through.
    public static Result Blend(decimal level, IReadOnlyList<decimal> priorScores, decimal momentumWeight, int minHistory)
    {
        if (priorScores.Count < minHistory)
            return new Result(Math.Clamp(level, -1m, 1m), null, priorScores.Count);

        var baseline = priorScores.Average();
        var delta = Math.Clamp(level - baseline, -1m, 1m);
        var blended = Math.Clamp(level + momentumWeight * delta, -1m, 1m);
        return new Result(blended, delta, priorScores.Count);
    }
}
