using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Trials;

// The Trials page's statistics (docs: transparency pivot, 6 Aug 2026) - every
// number the page shows is computed here, pure and directly unit-tested, from
// CLOSED trades only. The page's job is honesty: n is always reported, and
// callers grade evidence as too-early/suggestive/decisive from n, never from
// enthusiasm.
public static class TrialsMath
{
    public sealed record Band(string Label, int Trades, decimal AvgReturnPct, decimal WinRatePct);
    public sealed record FloorRow(decimal Floor, int Skipped, decimal SkippedAvgPct, int Kept, decimal KeptAvgPct);
    public sealed record TiltSummary(
        int ScoredTrades, int TiltedTrades,
        decimal EqualWeightedAvgPct, decimal TiltWeightedAvgPct, List<Band> Bands);

    private static decimal ReturnPct(Trade t) =>
        t.EntryPrice > 0 && t.ExitPrice is > 0 ? (t.ExitPrice.Value - t.EntryPrice) / t.EntryPrice * 100m : 0m;

    private static Band MakeBand(string label, List<Trade> trades) => new(
        label, trades.Count,
        trades.Count == 0 ? 0m : Math.Round(trades.Average(ReturnPct), 2),
        trades.Count == 0 ? 0m : Math.Round(100m * trades.Count(t => ReturnPct(t) > 0) / trades.Count, 1));

    // Forward-score bands: does the Claude forward score discriminate?
    public static List<Band> ForwardScoreBands(IReadOnlyList<Trade> closed)
    {
        (string Label, Func<decimal, bool> In)[] bands =
        [
            ("< 5", s => s < 5m),
            ("5 – 6", s => s is >= 5m and < 6m),
            ("6 – 7", s => s is >= 6m and < 7m),
            ("7 +", s => s >= 7m),
        ];
        var result = bands.Select(b => MakeBand(b.Label,
            closed.Where(t => t.ForwardScoreAtEntry is { } s && b.In(s)).ToList())).ToList();
        result.Add(MakeBand("unscored", closed.Where(t => t.ForwardScoreAtEntry is null).ToList()));
        return result;
    }

    // Gate/conviction bands: the technical score's discrimination (and the
    // known 8-band inversion, kept visible on purpose). Conviction lives on
    // the originating signal, not the Trade row - the caller supplies the
    // lookup.
    public static List<Band> ConvictionBands(IReadOnlyList<Trade> closed, Func<Trade, decimal?> conviction)
    {
        (string Label, Func<decimal, bool> In)[] bands =
        [
            ("< 6", s => s < 6m),
            ("6 – 7", s => s is >= 6m and < 7m),
            ("7 – 8", s => s is >= 7m and < 8m),
            ("8 +", s => s >= 8m),
        ];
        return bands.Select(b => MakeBand(b.Label,
            closed.Where(t => conviction(t) is { } s && b.In(s)).ToList())).ToList();
    }

    // Veto floor sweep: "if ForwardVetoFloor had been X, you would have
    // skipped n trades averaging y%". The floor EARNS raising only where the
    // skipped column is clearly negative at a real n.
    public static List<FloorRow> VetoFloorSweep(IReadOnlyList<Trade> closed)
    {
        var scored = closed.Where(t => t.ForwardScoreAtEntry is not null).ToList();
        var floors = new[] { 3m, 4m, 5m, 6m, 7m };
        return floors.Select(f =>
        {
            var skipped = scored.Where(t => t.ForwardScoreAtEntry!.Value < f).ToList();
            var kept = scored.Where(t => t.ForwardScoreAtEntry!.Value >= f).ToList();
            return new FloorRow(f,
                skipped.Count, skipped.Count == 0 ? 0m : Math.Round(skipped.Average(ReturnPct), 2),
                kept.Count, kept.Count == 0 ? 0m : Math.Round(kept.Average(ReturnPct), 2));
        }).ToList();
    }

    // Sizing tilt (F2): did the multiplier put more money on better trades?
    // Tilt-weighted vs equal-weighted average return over the SAME trades -
    // if they diverge positively the tilt earns its aggressiveness.
    public static TiltSummary SizingTilt(IReadOnlyList<Trade> closed)
    {
        var scored = closed.Where(t => t.ForwardScoreAtEntry is not null).ToList();
        var withMult = scored.Where(t => t.SizeMultiplier is not null).ToList();
        var tilted = withMult.Where(t => Math.Abs(t.SizeMultiplier!.Value - 1m) > 0.01m).ToList();

        var equal = scored.Count == 0 ? 0m : Math.Round(scored.Average(ReturnPct), 2);
        var weightSum = withMult.Sum(t => t.SizeMultiplier!.Value);
        var tiltWeighted = weightSum <= 0 ? 0m
            : Math.Round(withMult.Sum(t => ReturnPct(t) * t.SizeMultiplier!.Value) / weightSum, 2);

        var bands = new List<Band>
        {
            MakeBand("sized down (< 0.99x)", withMult.Where(t => t.SizeMultiplier < 0.99m).ToList()),
            MakeBand("neutral (~1x)", withMult.Where(t => t.SizeMultiplier is >= 0.99m and <= 1.01m).ToList()),
            MakeBand("sized up (> 1.01x)", withMult.Where(t => t.SizeMultiplier > 1.01m).ToList()),
        };
        return new TiltSummary(scored.Count, tilted.Count, equal, tiltWeighted, bands);
    }

    // Plain-language evidence grading shown on every card - the page's
    // defence against its own reader acting on n=20.
    public static string Grade(int n, int target) =>
        n >= target ? "decisive-n reached"
        : n >= target / 2 ? "suggestive — half the required evidence"
        : "far too early — do not act on this";
}
