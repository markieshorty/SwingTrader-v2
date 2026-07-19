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
        MaxOpenPositions = 3,
        DailyLossCircuitBreakerPct = 0.05m,
    };

    [Fact]
    public void Validate_DefaultProfile_DoesNotThrow()
    {
        var act = () => Valid().Validate();
        act.Should().NotThrow();
    }

    // ── Flat stop/target settings (replaced the per-setup/conviction tables) ──

    [Theory]
    [InlineData(0.01)]  // below 2% floor
    [InlineData(0.16)]  // above 15% ceiling
    public void Validate_StopLossPctOutOfRange_Throws(decimal pct)
    {
        var profile = Valid();
        profile.StopLossPct = pct;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Stop-loss*");
    }

    [Fact]
    public void Validate_TargetNotAboveStop_Throws()
    {
        var profile = Valid();
        profile.StopLossPct = 0.07m;
        profile.TargetPct = 0.07m;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*Target must exceed*");
    }

    [Fact]
    public void Validate_ValidatedLabConfig_SevenStopTenTarget_Passes()
    {
        var profile = Valid();
        profile.StopLossPct = 0.07m;
        profile.TargetPct = 0.10m;

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    // ── Position sizing (flat base, both modes) ───────────────────────────────

    [Fact]
    public void Validate_Sizing_WithinUnlockedShare_Passes()
    {
        // 15% x 2 = 30% <= 40% un-locked (locked 60%).
        var profile = Valid();
        profile.LockedCapitalPct = 0.60m;
        profile.FlatPositionPct = 0.15m;
        profile.MaxOpenPositions = 2;

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Sizing_ExceedsUnlockedShare_Throws()
    {
        // 25% x 3 = 75% > 40% un-locked: sizing never breaches the locked ceiling.
        var profile = Valid();
        profile.LockedCapitalPct = 0.60m;
        profile.FlatPositionPct = 0.25m;
        profile.MaxOpenPositions = 3;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*un-locked share*");
    }

    [Fact]
    public void Validate_Sizing_AppliesInFunnelModeToo()
    {
        // Funnel only tilts within the budget, so the same ceiling applies.
        var profile = Valid();
        profile.SizingMode = Core.Enums.PositionSizingMode.Funnel;
        profile.LockedCapitalPct = 0.60m;
        profile.FlatPositionPct = 0.25m;
        profile.MaxOpenPositions = 3;

        var act = () => profile.Validate();

        act.Should().Throw<ValidationException>().WithMessage("*un-locked share*");
    }

    [Theory]
    [InlineData(-0.01)]
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
        profile.FlatPositionPct = 0.03m; // keep 3 x 3% = 9% within the 10% un-locked share at 90% locked

        var act = () => profile.Validate();

        act.Should().NotThrow();
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
    [InlineData(0.90, 0.10, "Very Conservative")]
    [InlineData(0.75, 0.10, "Conservative")]
    [InlineData(0.60, 0.20, "Moderate-Aggressive")]
    [InlineData(0.60, 0.10, "Moderate")]
    public void RiskLabel_ReflectsLockedCapitalAndPositionSize(decimal lockedPct, decimal flatPositionPct, string expected)
    {
        var profile = new AccountRiskProfile { LockedCapitalPct = lockedPct, FlatPositionPct = flatPositionPct };

        profile.RiskLabel.Should().Be(expected);
    }
}
