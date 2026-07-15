using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Applying a Lab tactic winner back onto the account's live SetupTactics rows:
// uniform rule fields hit every setup, per-setup overrides win, and untouched
// rows are left alone - mirroring the backtester's MergeTactics precedence.
public class SetupTacticsRuleMapperTests
{
    private static List<SetupTactics> Rows() =>
    [
        new() { SetupType = SetupType.Breakout, StopLossPct = 0.05m, TargetPct = 0.08m, GuideHoldDays = 10, TrailingActivationPct = 0.05, TrailingDistancePct = 0.03 },
        new() { SetupType = SetupType.OversoldRecovery, StopLossPct = 0.05m, TargetPct = 0.08m, GuideHoldDays = 10, TrailingActivationPct = 0.05, TrailingDistancePct = 0.03 },
    ];

    [Fact]
    public void Apply_UniformRuleFields_HitEverySetup()
    {
        var rows = Rows();

        var changed = SetupTacticsRuleMapper.Apply(rows, new HistoricTradingRules(StopLossPct: 0.03m, MaxHoldDays: 15));

        changed.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.StopLossPct == 0.03m && r.GuideHoldDays == 15);
        rows.Should().OnlyContain(r => r.TargetPct == 0.08m); // untouched field preserved
    }

    [Fact]
    public void Apply_PerSetupOverride_WinsAndLeavesOthersAlone()
    {
        var rows = Rows();
        var rules = new HistoricTradingRules(SetupTactics:
        [
            new HistoricSetupTacticsOverride("Breakout", 0.07m, 0.20m, 25, 0.06m, 0.04m),
        ]);

        var changed = SetupTacticsRuleMapper.Apply(rows, rules);

        changed.Should().ContainSingle();
        var breakout = rows.Single(r => r.SetupType == SetupType.Breakout);
        breakout.GuideHoldDays.Should().Be(25);
        breakout.TargetPct.Should().Be(0.20m);
        var oversold = rows.Single(r => r.SetupType == SetupType.OversoldRecovery);
        oversold.GuideHoldDays.Should().Be(10); // untouched
    }

    [Fact]
    public void Apply_PerSetupOverride_BeatsUniformLayer()
    {
        var rows = Rows();
        var rules = new HistoricTradingRules(
            MaxHoldDays: 15, // uniform: 15 for everyone
            SetupTactics: [new HistoricSetupTacticsOverride("Breakout", 0.05m, 0.08m, 20, 0.05m, 0.03m)]);

        SetupTacticsRuleMapper.Apply(rows, rules);

        rows.Single(r => r.SetupType == SetupType.Breakout).GuideHoldDays.Should().Be(20);      // per-setup wins
        rows.Single(r => r.SetupType == SetupType.OversoldRecovery).GuideHoldDays.Should().Be(15); // uniform
    }

    [Fact]
    public void Apply_NoTacticFields_ChangesNothing()
    {
        var rows = Rows();

        var changed = SetupTacticsRuleMapper.Apply(rows, new HistoricTradingRules(MaxOpenPositions: 5, MinHoldDays: 4));

        changed.Should().BeEmpty();
        rows.Should().OnlyContain(r => r.StopLossPct == 0.05m && r.GuideHoldDays == 10);
    }
}
