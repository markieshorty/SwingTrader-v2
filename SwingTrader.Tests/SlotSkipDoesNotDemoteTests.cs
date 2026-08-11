using FluentAssertions;
using SwingTrader.Agents.Research;
using Xunit;

namespace SwingTrader.Tests;

// 11 Aug 2026: AVNT, ELAN and VAC all cleared the gate AND the forward
// threshold, and all three were demoted to Watch at 11:30 because SHOP was
// still open and MaxOpenPositions is 1. SHOP closed at 13:59. Signals are not
// rescored, so the freed slot went unused for the rest of the day.
//
// The demotion is gone: capacity is an execution-time fact, and
// PositionSizingService already refuses when open positions >= the max. These
// pin the seam that decides it, so the two ideas cannot be conflated again.
public class SlotSkipDoesNotDemoteTests
{
    [Theory]
    [InlineData(0, 0, 1, false, false)]  // empty book, one slot -> room
    [InlineData(1, 0, 1, false, true)]   // the 11 Aug case: SHOP open, no room
    [InlineData(0, 1, 1, false, true)]   // a pending intent occupies its slot
    [InlineData(1, 1, 3, false, false)]  // two of three used -> still room
    [InlineData(3, 0, 3, false, true)]
    public void IsPortfolioFull_CountsPendingIntentsAsOccupied(
        int open, int pending, int max, bool paused, bool expected) =>
        SlotGate.IsPortfolioFull(open, pending, max, paused).Should().Be(expected);

    [Fact]
    public void PausedEntries_LeaveNoUsableSlot_HoweverEmptyTheBook()
    {
        // Paused is not "full", but it has the same consequence for capacity,
        // which is why it rides the same predicate.
        SlotGate.IsPortfolioFull(openCount: 0, pendingCount: 0, maxOpenPositions: 5, entriesPaused: true)
            .Should().BeTrue();
    }

    // A full book governs whether stage 2 is worth paying for; it must never
    // govern whether the signal IS a Buy. Those are different questions asked
    // hours apart, and only the second one can be answered accurately at
    // scoring time.
    [Fact]
    public void TheGateIsAboutCostNotRecommendation()
    {
        SlotGate.SlotSkipSummary.Should().NotContain("demoted",
            "the slot gate defers stage-2 spend; it does not change the recommendation");
    }
}
