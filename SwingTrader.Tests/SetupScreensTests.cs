using FluentAssertions;
using SwingTrader.Agents.Watchlist;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

// docs/screener-union-plan. The old screen ranked the whole universe by one
// factor - today's absolute move - so it fed the two setups that need a big
// move (VolumeSpike's trigger literally requires dayChange > 1.5%, a subset of
// the screen's own >= 1% rule) and starved the two that need none at all
// (TrendFollowing and MomentumContinuation fire happily on a +0.3% day).
public class SetupScreensTests
{
    private static IndicatorResult Ind(
        decimal? rsi = null, decimal? macdHist = null, decimal? bbUpper = null,
        decimal? bbLower = null, decimal? bbMid = null, decimal? ema9 = null,
        decimal? ema21 = null, decimal? volumeRatio = null) =>
        new(rsi, null, null, macdHist, bbUpper, bbLower, bbMid, ema9, ema21, volumeRatio);

    [Fact]
    public void TrendFollowing_IsSurfacedWithNoVolumeAndNoMove()
    {
        // The whole point: this symbol would never clear the old 1% move floor.
        var ind = Ind(rsi: 58m, bbMid: 95m, ema9: 101m, ema21: 99m, volumeRatio: 0.8m);

        var found = SetupScreens.Evaluate("QUIET", ind, price: 100m, closeFourBarsAgo: 99.7m);

        found.Should().Contain(c => c.Setup == SetupType.TrendFollowing);
    }

    [Fact]
    public void MomentumContinuation_IsSurfacedOnAQuietDay()
    {
        var ind = Ind(rsi: 57m, macdHist: 0.4m, ema9: 101m, ema21: 99m, volumeRatio: 0.9m);

        var found = SetupScreens.Evaluate("QUIET2", ind, price: 100m, closeFourBarsAgo: 99.8m);

        found.Should().Contain(c => c.Setup == SetupType.MomentumContinuation);
    }

    [Fact]
    public void VolumeSpike_RanksOnVolumeAloneNotPriceMove()
    {
        // Ranking VolumeSpike by price move is exactly the coupling that made
        // the old screen feed it by construction.
        var quiet = SetupScreens.Evaluate("A", Ind(volumeRatio: 4m), 100m, 100m).ToList();
        var loud = SetupScreens.Evaluate("B", Ind(volumeRatio: 2m), 100m, 100m).ToList();

        var a = quiet.Single(c => c.Setup == SetupType.VolumeSpike);
        var b = loud.Single(c => c.Setup == SetupType.VolumeSpike);
        a.Score.Should().BeGreaterThan(b.Score);
    }

    [Fact]
    public void OversoldRecovery_RequiresTheRecoveryLeg()
    {
        // The 4-bar confirmation is the detector's falling-knife guard, so the
        // screen must not surface names still going down.
        var ind = Ind(rsi: 28m, bbLower: 90m);

        var recovering = SetupScreens.Evaluate("UP", ind, price: 100m, closeFourBarsAgo: 96m);
        var stillFalling = SetupScreens.Evaluate("DOWN", ind, price: 100m, closeFourBarsAgo: 104m);

        recovering.Should().Contain(c => c.Setup == SetupType.OversoldRecovery);
        stillFalling.Should().NotContain(c => c.Setup == SetupType.OversoldRecovery);
    }

    [Fact]
    public void OversoldRecovery_RanksDeeperOversoldHigher()
    {
        var deep = SetupScreens.Evaluate("DEEP", Ind(rsi: 22m, bbLower: 90m), 100m, 96m)
            .Single(c => c.Setup == SetupType.OversoldRecovery);
        var shallow = SetupScreens.Evaluate("SHALLOW", Ind(rsi: 38m, bbLower: 90m), 100m, 96m)
            .Single(c => c.Setup == SetupType.OversoldRecovery);

        deep.Score.Should().BeGreaterThan(shallow.Score);
    }

    [Fact]
    public void MomentumRanking_IsPriceScaleIndependent()
    {
        // A $400 stock and a $20 stock must compete on equal terms, or the
        // pool silently fills with expensive names.
        var cheap = SetupScreens.Evaluate("CHEAP",
            Ind(rsi: 55m, macdHist: 0.2m, ema9: 21m, ema21: 20m), 20m, 19m)
            .Single(c => c.Setup == SetupType.MomentumContinuation);
        var pricey = SetupScreens.Evaluate("PRICEY",
            Ind(rsi: 55m, macdHist: 4m, ema9: 410m, ema21: 400m), 400m, 390m)
            .Single(c => c.Setup == SetupType.MomentumContinuation);

        // Same histogram as a fraction of price => same score.
        cheap.Score.Should().Be(pricey.Score);
    }

    [Fact]
    public void Union_TakesTopNFromEachPoolSeparately()
    {
        // No cross-setup normalisation: each pool is ranked and topped on its
        // own scale, which is what a single weighted composite could not do.
        SetupCandidacy[] candidacies =
        [
            new("A", SetupType.TrendFollowing, 1m),
            new("B", SetupType.TrendFollowing, 2m),
            new("C", SetupType.VolumeSpike, 100m),
            new("D", SetupType.VolumeSpike, 200m),
        ];

        var union = SetupScreens.Union(candidacies, perSetup: 1);

        // The tiny TrendFollowing score still earns its slot despite being
        // two orders of magnitude below the VolumeSpike scores.
        union.Select(e => e.Symbol).Should().BeEquivalentTo(["B", "D"]);
    }

    [Fact]
    public void Union_InterleavesSetupsSoATrimCannotFavourOne()
    {
        // Only MaxCandidatesForClaude survive downstream, so the ORDER of the
        // union is really an allocation. A flat sort would let one pool take
        // every slot - which is the bias this whole design replaces.
        SetupCandidacy[] candidacies =
        [
            new("V1", SetupType.VolumeSpike, 900m),
            new("V2", SetupType.VolumeSpike, 800m),
            new("V3", SetupType.VolumeSpike, 700m),
            new("T1", SetupType.TrendFollowing, 0.9m),
            new("T2", SetupType.TrendFollowing, 0.8m),
            new("T3", SetupType.TrendFollowing, 0.7m),
        ];

        var union = SetupScreens.Union(candidacies, perSetup: 3);

        // Whatever the trim depth, both setups are represented.
        var firstFour = union.Take(4).Select(e => e.Symbol).ToList();
        firstFour.Should().Contain(s => s.StartsWith('V'));
        firstFour.Should().Contain(s => s.StartsWith('T'));
    }

    [Fact]
    public void Union_RecordsEverySetupThatSurfacedASymbol()
    {
        // Attribution is what makes per-setup outcomes judgeable later.
        SetupCandidacy[] candidacies =
        [
            new("MULTI", SetupType.TrendFollowing, 5m),
            new("MULTI", SetupType.MomentumContinuation, 5m),
        ];

        var union = SetupScreens.Union(candidacies, perSetup: 5);

        union.Single(e => e.Symbol == "MULTI").Setups.Should().BeEquivalentTo(
            new[] { SetupType.TrendFollowing, SetupType.MomentumContinuation });
    }

    [Fact]
    public void Union_IsEmptyWhenNothingIsTaken()
    {
        SetupScreens.Union([new("A", SetupType.Breakout, 1m)], perSetup: 0).Should().BeEmpty();
    }
}
