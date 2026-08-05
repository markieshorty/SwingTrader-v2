using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using SwingTrader.Agents.Execution;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Capital sleeves P1 (docs/sleeves-plan): allocation validation and the SPY
// core band-rebalance maths.
public class SleeveAllocationTests
{
    [Fact]
    public void Default_IsSwingOnly_AndValid()
    {
        var a = new AccountAllocation { AccountId = 1 };
        a.SwingPct.Should().Be(1m);
        a.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(0.5, 0, 0.5, true)]
    [InlineData(0.3, 0, 0.7, true)]
    [InlineData(0.5, 0, 0.4, false)]  // sums to 0.9
    [InlineData(-0.1, 0, 1.1, false)] // negative slice
    [InlineData(0.2, 0.2, 0.6, false)] // factor sleeve not available yet (P2)
    public void Validate_EnforcesTheContract(double spy, double factor, double swing, bool ok)
    {
        var a = new AccountAllocation
        {
            AccountId = 1,
            SpyCorePct = (decimal)spy,
            FactorTiltPct = (decimal)factor,
            SwingPct = (decimal)swing,
        };
        var act = () => a.Validate();
        if (ok) act.Should().NotThrow();
        else act.Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(1000, 1000, null)]     // on target
    [InlineData(1000, 960, null)]      // inside the 5% band
    [InlineData(1000, 940, 60.0)]      // outside: top up
    [InlineData(1000, 1080, -80.0)]    // outside: trim
    [InlineData(0, 0, null)]           // sleeve off, nothing held
    [InlineData(0, 500, -500.0)]       // sleeve turned off: sell down
    [InlineData(300, 290, null)]       // small sleeve: £25 floor beats 5%
    public void RebalanceDelta_TradesOnlyOutsideTheBand(double target, double current, double? expected)
    {
        var delta = SpyCoreService.RebalanceDelta((decimal)target, (decimal)current);
        if (expected is null) delta.Should().BeNull();
        else delta.Should().Be((decimal)expected);
    }
}
