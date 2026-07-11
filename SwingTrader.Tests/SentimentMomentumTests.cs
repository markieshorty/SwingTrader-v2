using FluentAssertions;
using SwingTrader.Agents.Research;
using Xunit;

namespace SwingTrader.Tests;

// The sentiment momentum blend: additive tilt from the symbol's own archived
// history, never a dilution - thin history passes the raw level through.
public class SentimentMomentumTests
{
    [Fact]
    public void Blend_InsufficientHistory_PassesRawLevelThrough()
    {
        var result = SentimentMomentum.Blend(0.6m, [0.1m, 0.2m], momentumWeight: 0.3m, minHistory: 3);

        result.BlendedScore.Should().Be(0.6m);
        result.Delta.Should().BeNull();
        result.HistoryCount.Should().Be(2);
    }

    [Fact]
    public void Blend_ImprovingSentiment_TiltsAboveTheRawLevel()
    {
        // Level 0.5 against a -0.1 average: delta +0.6, tilt +0.18.
        var result = SentimentMomentum.Blend(0.5m, [-0.2m, 0.0m, -0.1m], momentumWeight: 0.3m, minHistory: 3);

        result.Delta.Should().Be(0.6m);
        result.BlendedScore.Should().Be(0.68m);
    }

    [Fact]
    public void Blend_DeterioratingSentiment_TiltsBelowTheRawLevel()
    {
        // Level 0.1 against a 0.7 average: delta -0.6, tilt -0.18.
        var result = SentimentMomentum.Blend(0.1m, [0.8m, 0.6m, 0.7m], momentumWeight: 0.3m, minHistory: 3);

        result.Delta.Should().Be(-0.6m);
        result.BlendedScore.Should().Be(-0.08m);
    }

    [Fact]
    public void Blend_OutputAlwaysClampedToValidSentimentRange()
    {
        // Strong level + strong positive delta must not escape [-1, 1].
        var high = SentimentMomentum.Blend(0.95m, [-1m, -1m, -1m], momentumWeight: 0.5m, minHistory: 3);
        var low = SentimentMomentum.Blend(-0.95m, [1m, 1m, 1m], momentumWeight: 0.5m, minHistory: 3);

        high.BlendedScore.Should().Be(1m);
        low.BlendedScore.Should().Be(-1m);
    }

    [Fact]
    public void Blend_FlatHistory_LeavesLevelUntouched()
    {
        var result = SentimentMomentum.Blend(0.4m, [0.4m, 0.4m, 0.4m], momentumWeight: 0.3m, minHistory: 3);

        result.Delta.Should().Be(0m);
        result.BlendedScore.Should().Be(0.4m);
    }
}
