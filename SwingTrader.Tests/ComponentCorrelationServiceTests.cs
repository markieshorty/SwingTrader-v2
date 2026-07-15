using FluentAssertions;
using SwingTrader.Agents.Refinement;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Covers the two refinement bugs found in review: (1) FundamentalMomentum was
// missing from the analysed component set, so suggested weights normalised the
// other 7 to 1.0 and the untouched default pushed the total to 1.10, which
// StrategyWeights.Validate() rejects - every apply threw; (2) the adjustment
// used |r|, so a component whose high scores predicted LOSSES got its weight
// increased in proportion to how reliably wrong it was.
public class ComponentCorrelationServiceTests
{
    private readonly ComponentCorrelationService _sut = new();

    // 30 trades, half winners. `predictive` controls the Volume score's
    // relationship with the outcome; every other component sits at a constant
    // 0.55 (zero variance -> r=0 -> insignificant -> weight held).
    private static List<(StockSignal Signal, decimal ReturnPct)> BuildTrades(bool volumePredictsWinners)
    {
        var trades = new List<(StockSignal, decimal)>();
        for (var i = 0; i < 30; i++)
        {
            var isWinner = i % 2 == 0;
            var returnPct = isWinner ? 5m + (i % 5) : -5m - (i % 5);
            var volumeScore = (volumePredictsWinners == isWinner) ? 0.9m : 0.1m;
            trades.Add((new StockSignal
            {
                Symbol = $"SYM{i}",
                RsiScore = 0.55m,
                MacdScore = 0.55m,
                VolumeScore = volumeScore,
                SentimentComponentScore = 0.55m,
                SetupQualityScore = 0.55m,
                RelativeStrengthScore = 0.55m,
                PriceLevelScore = 0.55m,
                FundamentalMomentumScore = 0.55m,
            }, returnPct));
        }
        return trades;
    }

    [Fact]
    public void Analyse_SuggestedWeights_CoverTheSixGateComponents_AndPassValidate()
    {
        var result = _sut.Analyse(BuildTrades(volumePredictsWinners: true), new StrategyWeights(), 0.05m);

        // The six gate weights must normalise to sum 1.0 (and the forward blend
        // is carried through) so an apply never trips Validate().
        var act = () => result.SuggestedWeights.Validate();
        act.Should().NotThrow();

        result.Findings.Should().NotContain(f => f.ComponentName == "Sentiment");
        result.Findings.Should().NotContain(f => f.ComponentName == "FundamentalMomentum");
        result.Findings.Should().HaveCount(6);
    }

    [Fact]
    public void Analyse_PositivelyCorrelatedComponent_GetsWeightIncreased()
    {
        var current = new StrategyWeights();
        var result = _sut.Analyse(BuildTrades(volumePredictsWinners: true), current, 0.05m);

        var volume = result.Findings.Single(f => f.ComponentName == "Volume");
        volume.Correlation.Should().BeGreaterThan(0.5m);
        result.SuggestedWeights.VolumeWeight.Should().BeGreaterThan(current.VolumeWeight);
    }

    [Fact]
    public void Analyse_NegativelyCorrelatedComponent_GetsWeightDecreased_NotBoosted()
    {
        var current = new StrategyWeights();
        var result = _sut.Analyse(BuildTrades(volumePredictsWinners: false), current, 0.05m);

        var volume = result.Findings.Single(f => f.ComponentName == "Volume");
        volume.Correlation.Should().BeLessThan(-0.5m);

        // The |r| version pulled a perfectly anti-predictive component's
        // weight UP toward 0.9-ish implied weight; it must shrink instead.
        result.SuggestedWeights.VolumeWeight.Should().BeLessThan(current.VolumeWeight);
    }

    [Fact]
    public void Analyse_InsignificantCorrelations_HoldAllWeightsSteady()
    {
        // All components constant -> zero variance -> r=0 everywhere ->
        // nothing statistically significant -> no weight should move.
        var trades = BuildTrades(volumePredictsWinners: true)
            .Select(t =>
            {
                t.Signal.VolumeScore = 0.55m;
                return t;
            })
            .ToList();

        var current = new StrategyWeights();
        var result = _sut.Analyse(trades, current, 0.05m);

        result.SuggestedWeights.RsiWeight.Should().BeApproximately(current.RsiWeight, 0.001m);
        result.SuggestedWeights.MacdWeight.Should().BeApproximately(current.MacdWeight, 0.001m);
        result.SuggestedWeights.VolumeWeight.Should().BeApproximately(current.VolumeWeight, 0.001m);
        result.SuggestedWeights.SetupQualityWeight.Should().BeApproximately(current.SetupQualityWeight, 0.001m);
        result.SuggestedWeights.RelativeStrengthWeight.Should().BeApproximately(current.RelativeStrengthWeight, 0.001m);
        result.SuggestedWeights.PriceLevelWeight.Should().BeApproximately(current.PriceLevelWeight, 0.001m);
    }

    [Fact]
    public void Analyse_NullComponentScores_AreExcludedNotTreatedAsNeutral()
    {
        // Signals whose RelativeStrength was unavailable store null - those
        // must drop out of that component's sample rather than be read as 0.5.
        var trades = BuildTrades(volumePredictsWinners: true)
            .Select(t =>
            {
                t.Signal.RelativeStrengthScore = null;
                return t;
            })
            .ToList();

        var result = _sut.Analyse(trades, new StrategyWeights(), 0.05m);

        var rs = result.Findings.Single(f => f.ComponentName == "RelativeStrength");
        rs.Reasoning.Should().Contain("Not enough scored trades");
        var act = () => result.SuggestedWeights.Validate();
        act.Should().NotThrow();
    }
}
