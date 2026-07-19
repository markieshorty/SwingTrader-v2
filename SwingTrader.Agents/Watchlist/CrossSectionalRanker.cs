namespace SwingTrader.Agents.Watchlist;

// Cross-sectional selection percentile (docs discussion 18 Jul 2026, phase
// one of the ranking plan): every screened candidate is scored RELATIVE to
// the rest of that day's screened universe, from data the screener already
// holds (no extra API calls) - momentum magnitude and dollar volume. The
// percentile rides the candidate into the Claude selection prompt, onto the
// chosen WatchlistItem, and onto every StockSignal scored for that symbol -
// deliberately INERT for now (it drives no decision) so the scorecard can
// first prove whether high-percentile picks actually outperform before the
// gate or sizing ever act on it.
public static class CrossSectionalRanker
{
    // Blend of the two cross-sectional ranks. Momentum dominates because the
    // screener's whole premise is "meaningful move"; dollar volume rewards
    // moves backed by real participation rather than thin prints.
    private const decimal MomentumWeight = 0.6m;
    private const decimal DollarVolumeWeight = 0.4m;

    // Returns the same candidates with SelectionPercentile filled in
    // (0 = weakest of today's universe, 100 = strongest). Input order is
    // preserved. Fewer than 2 candidates can't be ranked against each other
    // and come back unstamped.
    public static List<ScreenedCandidate> StampPercentiles(List<ScreenedCandidate> candidates)
    {
        if (candidates.Count < 2) return candidates;

        var momentumRank = RankPositions(candidates, c => Math.Abs(c.ChangePercent));
        var dollarVolumeRank = RankPositions(candidates, c => c.LastPrice * c.Volume);

        var n = candidates.Count;
        return candidates.Select((c, i) =>
        {
            var blended = MomentumWeight * momentumRank[i] + DollarVolumeWeight * dollarVolumeRank[i];
            return c with { SelectionPercentile = Math.Round(blended / (n - 1) * 100m, 1) };
        }).ToList();
    }

    // Position of each candidate (by original index) when ordered ascending
    // by the metric: 0 = smallest, n-1 = largest. Ties share by first-come
    // ordering, which is fine at percentile granularity.
    private static decimal[] RankPositions(List<ScreenedCandidate> candidates, Func<ScreenedCandidate, decimal> metric)
    {
        var positions = new decimal[candidates.Count];
        var ordered = candidates
            .Select((c, i) => (Index: i, Value: metric(c)))
            .OrderBy(x => x.Value)
            .ToList();
        for (var pos = 0; pos < ordered.Count; pos++)
            positions[ordered[pos].Index] = pos;
        return positions;
    }
}
