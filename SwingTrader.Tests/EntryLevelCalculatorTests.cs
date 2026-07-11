using FluentAssertions;
using SwingTrader.Core.Trading;
using Xunit;

namespace SwingTrader.Tests;

// Stop/target maths after the 2026-07-12 rewrite: the per-setup and
// per-conviction tables are gone, replaced by the risk profile's flat
// StopLossPct/TargetPct. The calculator is now pure percentage arithmetic
// shared by Report (display levels), Execution (order levels) and the
// backtester - identical distances from whatever price each caller holds.
public class EntryLevelCalculatorTests
{
    [Theory]
    [InlineData(100, 0.05, 0.08, 95.00, 108.00)]   // defaults
    [InlineData(100, 0.07, 0.10, 93.00, 110.00)]   // the Lab-validated config
    [InlineData(55.55, 0.05, 0.08, 52.77, 59.99)]  // rounding to 2dp
    public void Calculate_AppliesFlatPercentages(
        double price, double stop, double target, double expectedStop, double expectedTarget)
    {
        var (stopLoss, targetPrice) = EntryLevelCalculator.Calculate((decimal)price, (decimal)stop, (decimal)target);

        stopLoss.Should().Be((decimal)expectedStop);
        targetPrice.Should().Be((decimal)expectedTarget);
    }

    [Fact]
    public void Calculate_SameDistancesRegardlessOfPriceSnapshot()
    {
        // Report computes levels off its price, Execution off a fresher one -
        // the PERCENTAGE distances must be identical either way.
        var (reportStop, reportTarget) = EntryLevelCalculator.Calculate(50m, 0.05m, 0.08m);
        var (executionStop, executionTarget) = EntryLevelCalculator.Calculate(55m, 0.05m, 0.08m);

        (reportStop / 50m).Should().Be(executionStop / 55m);
        (reportTarget / 50m).Should().Be(executionTarget / 55m);
    }
}
