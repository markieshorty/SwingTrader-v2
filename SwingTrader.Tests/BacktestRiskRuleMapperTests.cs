using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class BacktestRiskRuleMapperTests
{
    [Fact]
    public void Apply_OverwritesOnlyNonNullRuleFields()
    {
        var profile = new AccountRiskProfile
        {
            MaxHoldDays = 20, MinHoldDays = 5, MaxOpenPositions = 8,
            TrailingActivationPct = 0.05, TrailingDistancePct = 0.03,
            StopLossPct = 0.08m, TargetPct = 0.20m,
            MomentumHealthThreshold = 0.4m, MaxPositionPctOfActive = 0.25m,
        };

        // Only three fields overridden by the A/B run.
        var rules = new HistoricTradingRules(MaxHoldDays: 12, StopLossPct: 0.05m, MomentumHealthThreshold: 0.55m);

        BacktestRiskRuleMapper.Apply(profile, rules);

        // Overridden
        profile.MaxHoldDays.Should().Be(12);
        profile.StopLossPct.Should().Be(0.05m);
        profile.MomentumHealthThreshold.Should().Be(0.55m);
        // Untouched (rule field was null → keep live value)
        profile.MinHoldDays.Should().Be(5);
        profile.MaxOpenPositions.Should().Be(8);
        profile.TrailingActivationPct.Should().Be(0.05);
        profile.TargetPct.Should().Be(0.20m);
        profile.MaxPositionPctOfActive.Should().Be(0.25m);
    }

    [Fact]
    public void Apply_TrailingPcts_ConvertDecimalToDouble()
    {
        var profile = new AccountRiskProfile { TrailingActivationPct = 0.01, TrailingDistancePct = 0.01 };
        BacktestRiskRuleMapper.Apply(profile, new HistoricTradingRules(
            TrailingActivationPct: 0.06m, TrailingDistancePct: 0.04m));

        profile.TrailingActivationPct.Should().BeApproximately(0.06, 1e-9);
        profile.TrailingDistancePct.Should().BeApproximately(0.04, 1e-9);
    }

    [Fact]
    public void Apply_EmptyRules_ChangesNothing()
    {
        var profile = new AccountRiskProfile { MaxHoldDays = 20, StopLossPct = 0.08m };
        BacktestRiskRuleMapper.Apply(profile, new HistoricTradingRules());

        profile.MaxHoldDays.Should().Be(20);
        profile.StopLossPct.Should().Be(0.08m);
    }
}
