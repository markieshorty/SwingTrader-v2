using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Conviction ceiling (31 Jul 2026): Buys scoring ABOVE the ceiling demote to
// Watch / never enter. 0 = off. Motivated by the conviction-8 bucket losing
// money on oversold-recovery setups; meant to be proven out-of-sample.
public class ConvictionCeilingTests
{
    [Fact]
    public void Validate_RejectsOutOfRangeCeiling()
    {
        var profile = new AccountRiskProfile { AccountId = 1, MaxConvictionForBuy = 11m };
        var act = () => profile.Validate();
        act.Should().Throw<System.ComponentModel.DataAnnotations.ValidationException>()
            .WithMessage("*Conviction ceiling*");
    }

    [Theory]
    [InlineData(0)]   // off
    [InlineData(8)]   // typical experiment value
    [InlineData(10)]  // upper bound
    public void Validate_AcceptsValidCeiling(double ceiling)
    {
        var profile = new AccountRiskProfile { AccountId = 1, MaxConvictionForBuy = (decimal)ceiling };
        var act = () => profile.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void RuleMapper_AppliesCeilingToProfile()
    {
        var profile = new AccountRiskProfile { AccountId = 1 };
        BacktestRiskRuleMapper.Apply(profile, new HistoricTradingRules(MaxConvictionForBuy: 8m));
        profile.MaxConvictionForBuy.Should().Be(8m);
    }

    [Fact]
    public void RuleMapper_NullCeiling_LeavesProfileValue()
    {
        var profile = new AccountRiskProfile { AccountId = 1, MaxConvictionForBuy = 7.5m };
        BacktestRiskRuleMapper.Apply(profile, new HistoricTradingRules(MaxHoldDays: 12));
        profile.MaxConvictionForBuy.Should().Be(7.5m);
    }
}
