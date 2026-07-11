using FluentAssertions;
using SwingTrader.Agents.Research;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using Xunit;

namespace SwingTrader.Tests;

// The bounded conviction adjustment for a Claude-detected forward catalyst:
// bounded, defensive about earnings/undated/stale events, and a no-op when
// nothing was detected. Mirrors the post-earnings adjustment's contract.
public class CatalystAdjustmentTests
{
    private static readonly DateOnly Today = new(2026, 7, 12);

    private static decimal Apply(ClaudeCatalystResult? catalyst, out string? reasoning, decimal conviction = 6.0m) =>
        ResearchPipeline.ApplyCatalystAdjustment(
            conviction, catalyst, maxBoost: 0.5m, maxPenalty: 0.5m, maxDaysAhead: 30, Today, out reasoning);

    [Fact]
    public void NullOrUndetected_LeavesConvictionUntouched()
    {
        Apply(null, out var r1).Should().Be(6.0m);
        r1.Should().BeNull();

        Apply(new ClaudeCatalystResult(false, null, null, null, 0f), out var r2).Should().Be(6.0m);
        r2.Should().BeNull();
    }

    [Fact]
    public void BullishCatalyst_BoostsProportionallyToStrength_AndExplainsItself()
    {
        var catalyst = new ClaudeCatalystResult(true, "FDA decision", "2026-07-25", "bullish", 0.8f);

        var adjusted = Apply(catalyst, out var reasoning);

        adjusted.Should().Be(6.4m); // 0.8 * 0.5 cap
        reasoning.Should().Contain("FDA decision").And.Contain("added 0.4");
    }

    [Fact]
    public void BearishCatalyst_ReducesConviction()
    {
        var catalyst = new ClaudeCatalystResult(true, "plant closure", "2026-07-20", "bearish", 1.0f);

        var adjusted = Apply(catalyst, out var reasoning);

        adjusted.Should().Be(5.5m);
        reasoning.Should().Contain("Bearish catalyst");
    }

    [Fact]
    public void EarningsTypedCatalyst_IsRejected_TheEarningsGateOwnsThose()
    {
        var catalyst = new ClaudeCatalystResult(true, "Q3 earnings report", "2026-07-20", "bullish", 1.0f);

        Apply(catalyst, out var reasoning).Should().Be(6.0m);
        reasoning.Should().BeNull();
    }

    [Theory]
    [InlineData("2026-07-01")]  // in the past
    [InlineData("2026-12-01")]  // beyond the 30-day horizon
    [InlineData("not-a-date")]  // unparsable
    public void StaleFarOrUnparsableDates_AreRejected(string date)
    {
        var catalyst = new ClaudeCatalystResult(true, "product launch", date, "bullish", 1.0f);

        Apply(catalyst, out var reasoning).Should().Be(6.0m);
        reasoning.Should().BeNull();
    }

    [Fact]
    public void UndatedCatalyst_StillCounts_ClaudeCouldNotPinTheDay()
    {
        // The prompt asks for a date when inferable, but "guidance raise
        // announced, effective imminently" style events often have none -
        // detected + direction + strength is enough.
        var catalyst = new ClaudeCatalystResult(true, "guidance raise", null, "bullish", 0.6f);

        Apply(catalyst, out var reasoning).Should().Be(6.3m);
        reasoning.Should().NotBeNull();
    }

    [Fact]
    public void StrengthIsClamped_AndConvictionStaysInRange()
    {
        var overStrength = new ClaudeCatalystResult(true, "contract win", null, "bullish", 5.0f);
        Apply(overStrength, out _, conviction: 9.9m).Should().Be(10.0m); // clamped strength, capped ceiling

        var bearish = new ClaudeCatalystResult(true, "recall", null, "bearish", 1.0f);
        Apply(bearish, out _, conviction: 0.2m).Should().Be(0.0m); // floor
    }
}
