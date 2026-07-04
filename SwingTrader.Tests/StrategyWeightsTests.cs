using FluentAssertions;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class StrategyWeightsTests
{
    [Fact]
    public void Validate_DoesNotThrow_WhenWeightsSumToOne()
    {
        var weights = new StrategyWeights();

        var act = weights.Validate;

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Throws_WhenWeightsDoNotSumToOne()
    {
        var weights = new StrategyWeights { RsiWeight = 0.5m };

        var act = weights.Validate;

        act.Should().Throw<InvalidOperationException>();
    }
}
