using FluentAssertions;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class PositionMonitorServiceTests
{
    // Real calendar so the time-exit path counts trading days (weekends/
    // holidays excluded). Time-based cases below use wide margins so they
    // don't sit on the trading-day boundary whatever day the suite runs.
    private readonly PositionMonitorService _sut = new(new MarketCalendarService());

    private static Trade MakeTrade(decimal entry, decimal stopLoss, decimal target, decimal? trailingStop = null, DateTime? openedAt = null) =>
        new()
        {
            Symbol = "AAPL",
            EntryPrice = entry,
            StopLossPrice = stopLoss,
            TargetPrice = target,
            TrailingStopPrice = trailingStop,
            OpenedAt = openedAt ?? DateTime.UtcNow,
            Quantity = 10,
        };

    [Fact]
    public async Task CheckPositionAsync_PriceAtOrBelowStopLoss_ReturnsStopLossHit()
    {
        var trade = MakeTrade(entry: 100m, stopLoss: 95m, target: 120m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 94m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.StopLossHit);
    }

    [Fact]
    public async Task CheckPositionAsync_PriceAtOrAboveTarget_ReturnsTargetHit()
    {
        var trade = MakeTrade(entry: 100m, stopLoss: 95m, target: 120m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 121m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.TargetHit);
    }

    [Fact]
    public async Task CheckPositionAsync_StopLossPriority_BeatsTargetWhenBothSomehowCross()
    {
        // Pathological config (target <= stop) still resolves deterministically -
        // stop loss is checked first, so it wins regardless of price ordering.
        var trade = MakeTrade(entry: 100m, stopLoss: 130m, target: 120m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 125m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.StopLossHit);
    }

    [Fact]
    public async Task CheckPositionAsync_PriceCrossesActivationThreshold_ArmsTrailingStopWithoutExiting()
    {
        // 5% activation on a $100 entry = $105; trailing distance 3% below current price.
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 106m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.None);
        result.UpdatedTrailingStop.Should().Be(106m * 0.97m);
    }

    [Fact]
    public async Task CheckPositionAsync_BelowActivationThreshold_DoesNotArmTrailingStop()
    {
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 104m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.None);
        result.UpdatedTrailingStop.Should().BeNull();
    }

    [Fact]
    public async Task CheckPositionAsync_PriceDropsToArmedTrailingStop_ReturnsTrailingStopHit()
    {
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m, trailingStop: 106m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 106m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.TrailingStopHit);
    }

    [Fact]
    public async Task CheckPositionAsync_PriceRisesFurtherWithArmedTrail_RatchetsTrailUpward()
    {
        // Already armed at 106; price keeps climbing to 115 - the trail should
        // ratchet up to 115 * 0.97 rather than staying at the old level, and
        // never exit while it's still ratcheting.
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m, trailingStop: 106m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 115m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.None);
        result.UpdatedTrailingStop.Should().Be(115m * 0.97m);
    }

    [Fact]
    public async Task CheckPositionAsync_ArmedTrailButPriceStagnant_NeitherRatchetsNorExits()
    {
        // Price sits between the old trail level and a small ratchet-worthy
        // rise - not low enough to hit the trail, not high enough to raise it.
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m, trailingStop: 106m);

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 107m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.None);
        result.UpdatedTrailingStop.Should().BeNull();
    }

    [Fact]
    public async Task CheckPositionAsync_HeldPastMaxDaysBetweenStopAndTarget_ReturnsTimeExit()
    {
        // 102 is deliberately below the 5%-activation threshold (105) so the
        // trailing stop never arms and Step 4 (time exit) is actually reached.
        // 25 calendar days back is comfortably >10 trading days even with
        // holidays, so the trading-day count clears maxHoldDays=10.
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m, openedAt: DateTime.UtcNow.AddDays(-25));

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 102m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.TimeExit);
    }

    [Fact]
    public async Task CheckPositionAsync_WithinMaxDays_DoesNotTimeExit()
    {
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m, openedAt: DateTime.UtcNow.AddDays(-5));

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 105m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.None);
    }

    [Fact]
    public async Task CheckPositionAsync_HeldPastMaxDaysButAtStopOrTarget_PrefersStopTargetOverTimeExit()
    {
        // Stop loss takes priority even when the hold period is also exceeded -
        // Step 1 returns before Step 4's time-exit check ever runs.
        var trade = MakeTrade(entry: 100m, stopLoss: 95m, target: 200m, openedAt: DateTime.UtcNow.AddDays(-15));

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 95m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.StopLossHit);
    }

    [Fact]
    public async Task CheckPositionAsync_NoConditionsMet_ReturnsNone()
    {
        var trade = MakeTrade(entry: 100m, stopLoss: 90m, target: 200m, openedAt: DateTime.UtcNow.AddDays(-2));

        var result = await _sut.CheckPositionAsync(trade, currentPrice: 101m, maxHoldDays: 10, trailingActivationPct: 0.05, trailingDistancePct: 0.03);

        result.Reason.Should().Be(ExitReason.None);
        result.UpdatedTrailingStop.Should().BeNull();
    }
}
