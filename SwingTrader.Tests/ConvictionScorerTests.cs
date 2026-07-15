using FluentAssertions;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class ConvictionScorerTests
{
    [Fact]
    public void ScoreRsi_NullValue_ReturnsNeutral()
    {
        ConvictionScorer.ScoreRsi(null).Should().Be(0.5m);
    }

    [Theory]
    [InlineData(20, 0.5)] // Deeply oversold - flat floor, not automatically bullish
    [InlineData(30, 0.75)] // Midway through the recovery-favoring band
    [InlineData(50, 0.625)] // Neutral RSI, midway between the 45-55 band's endpoints
    [InlineData(80, 0.0)] // Deeply overbought - flat ceiling at 0
    public void ScoreRsi_KnownBands_MapToExpectedScore(decimal rsi, decimal expected)
    {
        ConvictionScorer.ScoreRsi(rsi).Should().Be(expected);
    }

    [Fact]
    public void ScoreMacd_NullHistogram_ReturnsNeutral()
    {
        ConvictionScorer.ScoreMacd(null, null).Should().Be(0.5m);
    }

    [Fact]
    public void ScoreMacd_PositiveAndRising_ScoresHighest()
    {
        ConvictionScorer.ScoreMacd(histogram: 1.0m, previousHistogram: 0.5m).Should().Be(1.0m);
    }

    [Fact]
    public void ScoreMacd_PositiveButFalling_ScoresModerate()
    {
        ConvictionScorer.ScoreMacd(histogram: 0.5m, previousHistogram: 1.0m).Should().Be(0.6m);
    }

    [Fact]
    public void ScoreMacd_NegativeButRising_ScoresLowModerate()
    {
        ConvictionScorer.ScoreMacd(histogram: -0.5m, previousHistogram: -1.0m).Should().Be(0.3m);
    }

    [Fact]
    public void ScoreMacd_NegativeAndFalling_ScoresZero()
    {
        ConvictionScorer.ScoreMacd(histogram: -1.0m, previousHistogram: -0.5m).Should().Be(0.0m);
    }

    [Fact]
    public void ScoreVolume_NullValue_ReturnsNeutral()
    {
        ConvictionScorer.ScoreVolume(null).Should().Be(0.5m);
    }

    [Fact]
    public void ScoreVolume_DoubleAverageOrMore_ScoresMaximum()
    {
        ConvictionScorer.ScoreVolume(2.5m).Should().Be(1.0m);
    }

    [Fact]
    public void ScoreVolume_BelowAverage_ScoresBelowNeutral()
    {
        ConvictionScorer.ScoreVolume(0.5m).Should().BeLessThan(0.5m);
    }

    [Fact]
    public void ScoreSentiment_NullValue_ReturnsNeutral()
    {
        ConvictionScorer.ScoreSentiment(null).Should().Be(0.5m);
    }

    [Theory]
    [InlineData(1.0, 1.0)] // Maximally positive sentiment
    [InlineData(-1.0, 0.0)] // Maximally negative sentiment
    [InlineData(0.0, 0.5)] // Neutral sentiment
    public void ScoreSentiment_MapsMinusOneToOneRangeOntoZeroToOne(decimal sentiment, decimal expected)
    {
        ConvictionScorer.ScoreSentiment(sentiment).Should().Be(expected);
    }

    [Fact]
    public void ScoreSentiment_OutOfRangeValue_ClampsToZeroToOne()
    {
        ConvictionScorer.ScoreSentiment(5.0m).Should().Be(1.0m);
        ConvictionScorer.ScoreSentiment(-5.0m).Should().Be(0.0m);
    }

    [Theory]
    [InlineData(SetupType.OversoldRecovery, 1.0)]
    [InlineData(SetupType.Breakout, 0.9)]
    [InlineData(SetupType.MomentumContinuation, 0.75)]
    [InlineData(SetupType.VolumeSpike, 0.6)]
    [InlineData(SetupType.TrendFollowing, 0.5)]
    [InlineData(SetupType.Unknown, 0.0)]
    public void ScoreSetupQuality_MapsEachSetupTypeToItsExpectedScore(SetupType setup, decimal expected)
    {
        ConvictionScorer.ScoreSetupQuality(setup).Should().Be(expected);
    }

    // Six gate weights summing to 1.0 (sentiment/fundamental aren't part of the gate).
    private static StrategyWeights EqualWeights() => new()
    {
        RsiWeight = 0.17m, MacdWeight = 0.17m, VolumeWeight = 0.17m,
        SetupQualityWeight = 0.17m, RelativeStrengthWeight = 0.16m, PriceLevelWeight = 0.16m,
    };

    [Fact]
    public void Calculate_AllComponentsMaxed_ReturnsTenOnATenPointScale()
    {
        var weights = EqualWeights();

        var result = ConvictionScorer.Calculate(weights, 1.0m, 1.0m, 1.0m, 1.0m, 1.0m, 1.0m);

        result.Should().Be(10.0m);
    }

    [Fact]
    public void Calculate_AllComponentsZero_ReturnsZero()
    {
        var weights = EqualWeights();

        var result = ConvictionScorer.Calculate(weights, 0m, 0m, 0m, 0m, 0m, 0m);

        result.Should().Be(0.0m);
    }

    [Fact]
    public void Calculate_UnspecifiedRelativeStrengthAndPriceLevel_DefaultToNeutral()
    {
        // Callers not passing relative strength / price level must still get a
        // sensible neutral 0.5 contribution, not zero.
        var weights = EqualWeights();

        var withExplicitNeutral = ConvictionScorer.Calculate(weights, 1.0m, 1.0m, 1.0m, 1.0m, 0.5m, 0.5m);
        var withDefaults = ConvictionScorer.Calculate(weights, 1.0m, 1.0m, 1.0m, 1.0m);

        withDefaults.Should().Be(withExplicitNeutral);
    }

    [Fact]
    public void Calculate_ResultNeverExceedsTenPointScale()
    {
        // Weights summing to > 1.0 shouldn't be possible in practice (validated
        // elsewhere), but the clamp is a deliberate last line of defence.
        var weights = EqualWeights();
        weights.RsiWeight = 5.0m;

        var result = ConvictionScorer.Calculate(weights, 1.0m, 1.0m, 1.0m, 1.0m, 1.0m, 1.0m);

        result.Should().Be(10.0m);
    }
}
