using FluentAssertions;
using SwingTrader.Agents.Research;
using Xunit;

namespace SwingTrader.Tests;

// Slot-aware research gate (docs/on-demand-research P1): stage-2 scoring is
// skipped when the account has nowhere to put a Buy.
public class SlotGateTests
{
    [Theory]
    [InlineData(2, 0, 2, false, true)]  // full on open positions alone
    [InlineData(1, 1, 2, false, true)]  // pending intent occupies a slot
    [InlineData(1, 0, 2, false, false)] // one slot genuinely free
    [InlineData(0, 0, 2, false, false)] // empty book
    [InlineData(3, 0, 2, false, true)]  // over-full (adopted/manual) still full
    [InlineData(0, 0, 2, true, true)]   // paused entries = zero usable slots
    public void IsPortfolioFull_CountsOpenPlusPendingAgainstMax(
        int open, int pending, int max, bool paused, bool expected)
    {
        SlotGate.IsPortfolioFull(open, pending, max, paused).Should().Be(expected);
    }
}
