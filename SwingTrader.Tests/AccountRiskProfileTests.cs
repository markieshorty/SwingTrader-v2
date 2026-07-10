using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class AccountRiskProfileTests
{
    private static AccountRiskProfile Valid() => new()
    {
        LockedCapitalPct = 0.70m,
        MaxPositionPctOfActive = 0.20m,
        MaxOpenPositions = 3,
        DailyLossCircuitBreakerPct = 0.05m,
        Tier1UnlockMinTrades = 30,
        Tier1UnlockMinWinRate = 0.55m,
        Tier2UnlockMinTrades = 60,
        Tier2UnlockMinWinRate = 0.58m,
    };

    [Fact]
    public void Validate_DefaultProfile_DoesNotThrow()
    {
        var act = () => Valid().Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(0.91)]
    public void Validate_LockedCapitalPctOutOfRange_Throws(decimal pct)
    {
        var profile = Valid();
        profile.LockedCapitalPct = pct;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Locked capital*");
    }

    [Theory]
    [InlineData(0.50)]
    [InlineData(0.90)]
    public void Validate_LockedCapitalPctAtBoundary_DoesNotThrow(decimal pct)
    {
        var profile = Valid();
        profile.LockedCapitalPct = pct;
        profile.MaxPositionPctOfActive = 0.05m; // keep within reduced active capital

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.04)]
    [InlineData(0.41)]
    public void Validate_MaxPositionPctOfActiveOutOfRange_Throws(decimal pct)
    {
        var profile = Valid();
        profile.MaxPositionPctOfActive = pct;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Max position*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Validate_MaxOpenPositionsOutOfRange_Throws(int value)
    {
        var profile = Valid();
        profile.MaxOpenPositions = value;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Max positions*");
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.16)]
    public void Validate_DailyLossCircuitBreakerOutOfRange_Throws(decimal pct)
    {
        var profile = Valid();
        profile.DailyLossCircuitBreakerPct = pct;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Circuit breaker*");
    }

    [Theory]
    [InlineData(19)]
    [InlineData(101)]
    public void Validate_Tier1UnlockMinTradesOutOfRange_Throws(int value)
    {
        var profile = Valid();
        profile.Tier1UnlockMinTrades = value;
        if (value > profile.Tier2UnlockMinTrades) profile.Tier2UnlockMinTrades = value + 10;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Tier 1 min trades*");
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(0.81)]
    public void Validate_Tier1UnlockMinWinRateOutOfRange_Throws(decimal value)
    {
        var profile = Valid();
        profile.Tier1UnlockMinWinRate = value;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Tier 1 min win rate*");
    }

    [Fact]
    public void Validate_Tier2UnlockMinTradesNotGreaterThanTier1_Throws()
    {
        var profile = Valid();
        profile.Tier1UnlockMinTrades = 50;
        profile.Tier2UnlockMinTrades = 50;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Tier 2 min trades*");
    }

    [Fact]
    public void Validate_Tier2UnlockMinTradesExceedsMax_Throws()
    {
        var profile = Valid();
        profile.Tier2UnlockMinTrades = 201;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Tier 2 min trades*");
    }

    [Fact]
    public void Validate_Tier2UnlockMinWinRateNotGreaterThanTier1_Throws()
    {
        var profile = Valid();
        profile.Tier1UnlockMinWinRate = 0.60m;
        profile.Tier2UnlockMinWinRate = 0.60m;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Tier 2 min win rate*");
    }

    [Fact]
    public void Validate_Tier2UnlockMinWinRateExceedsMax_Throws()
    {
        var profile = Valid();
        profile.Tier2UnlockMinWinRate = 0.86m;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Tier 2 min win rate*");
    }

    [Fact]
    public void Validate_MaxPositionExceedsActiveCapital_Throws()
    {
        var profile = Valid();
        profile.LockedCapitalPct = 0.90m; // active capital = 10%
        profile.MaxPositionPctOfActive = 0.33m; // exceeds it

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*active capital*");
    }

    [Fact]
    public void Validate_MinHoldDaysBelowAbsoluteFloor_Throws()
    {
        var profile = Valid();
        profile.MinHoldDays = 0;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Probation period must be at least*");
    }

    [Theory]
    [InlineData(0.19)]
    [InlineData(0.61)]
    public void Validate_MomentumHealthThresholdOutOfRange_Throws(decimal value)
    {
        var profile = Valid();
        profile.MomentumHealthThreshold = value;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Momentum health threshold*");
    }

    [Fact]
    public void Validate_MinHoldDaysEqualsMaxHoldDays_Throws()
    {
        var profile = Valid();
        profile.MinHoldDays = 10;
        profile.MaxHoldDays = 10;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Probation period (10d) must be less than maximum hold period (10d)*");
    }

    [Fact]
    public void Validate_MinHoldDaysGreaterThanMaxHoldDays_Throws()
    {
        var profile = Valid();
        profile.MinHoldDays = 12;
        profile.MaxHoldDays = 10;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Probation period (12d) must be less than maximum hold period (10d)*");
    }

    [Fact]
    public void Validate_MinHoldDaysOneLessThanMax_DoesNotThrow()
    {
        var profile = Valid();
        profile.MinHoldDays = 9;
        profile.MaxHoldDays = 10;

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.90, 0.20, "Very Conservative")]
    [InlineData(0.75, 0.20, "Conservative")]
    [InlineData(0.60, 0.30, "Moderate-Aggressive")]
    [InlineData(0.60, 0.10, "Moderate")]
    public void RiskLabel_ReflectsLockedCapitalAndPositionSize(decimal lockedPct, decimal maxPositionPct, string expected)
    {
        var profile = new AccountRiskProfile { LockedCapitalPct = lockedPct, MaxPositionPctOfActive = maxPositionPct };

        profile.RiskLabel.Should().Be(expected);
    }
}
