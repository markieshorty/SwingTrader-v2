using System.Globalization;
using FluentAssertions;
using SwingTrader.Agents.Scorecard;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class DialSetTests
{
    private static AccountRiskProfile Profile(decimal stop = 0.15m) => new()
    {
        StopLossPct = stop,
        TargetPct = 0.25m,
        MaxHoldDays = 30,
        TrailingActivationPct = 0.125,
        TrailingDistancePct = 0.075,
    };

    private static SetupTactics Tactics(SetupType setup, decimal stop) => new()
    {
        SetupType = setup,
        StopLossPct = stop,
        TargetPct = 0.25m,
        GuideHoldDays = 30,
        TrailingActivationPct = 0.075,
        TrailingDistancePct = 0.015,
    };

    [Fact]
    public void Version_IsStableAcrossCalls()
    {
        var a = DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile());
        var b = DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile());

        a.Version.Should().Be(b.Version);
    }

    // Dictionary enumeration order is not guaranteed. If it leaked into the
    // version, one dial set would present as several and silently fragment the
    // calibration population across them.
    [Fact]
    public void Version_IsIndependentOfTacticsOrdering()
    {
        var forward = DialSet.FromAccount(
            [Tactics(SetupType.Breakout, 0.15m), Tactics(SetupType.VolumeSpike, 0.12m)], Profile());
        var reversed = DialSet.FromAccount(
            [Tactics(SetupType.VolumeSpike, 0.12m), Tactics(SetupType.Breakout, 0.15m)], Profile());

        forward.Version.Should().Be(reversed.Version);
    }

    [Fact]
    public void Version_ChangesWhenAnyDialChanges()
    {
        var baseline = DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile());

        DialSet.FromAccount([Tactics(SetupType.Breakout, 0.16m)], Profile())
            .Version.Should().NotBe(baseline.Version, "a per-setup dial moved");

        DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile(stop: 0.20m))
            .Version.Should().NotBe(baseline.Version, "the fallback profile moved");
    }

    // A version that shifted with the host's culture would split the population
    // between machines - and the Functions host and a dev box need not match.
    [Fact]
    public void Version_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-GB");
            var gb = DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile()).Version;

            // de-DE uses a comma as the decimal separator.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var de = DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile()).Version;

            gb.Should().Be(de);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void For_FallsBackToProfileWhenSetupHasNoTactics()
    {
        var set = DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile(stop: 0.20m));

        set.For(SetupType.Breakout).StopLossPct.Should().Be(0.15m);
        set.For(SetupType.VolumeSpike).StopLossPct.Should().Be(0.20m, "no tactics row - use the profile");
    }

    // The prefix marks the dial semantics. When SPEC P4 moves these to ATR
    // multiples, the new sets must not be silently comparable with these.
    [Fact]
    public void Version_IsPrefixedWithItsUnitSemantics()
    {
        DialSet.FromAccount([Tactics(SetupType.Breakout, 0.15m)], Profile())
            .Version.Should().StartWith("pct-");
    }
}
