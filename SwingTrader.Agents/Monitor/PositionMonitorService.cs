using SwingTrader.Core.Constants;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Market;

namespace SwingTrader.Agents.Monitor;

public class PositionMonitorService(IMarketCalendarService marketCalendar) : IPositionMonitorService
{
    public Task<PositionCheckResult> CheckPositionAsync(
        Trade trade,
        decimal currentPrice,
        int maxHoldDays,
        double trailingActivationPct,
        double trailingDistancePct,
        CancellationToken ct = default)
    {
        // A non-positive price is a FAILED quote (unknown symbol, feed outage),
        // never a real market price - acting on it sold a healthy position as a
        // "$0.00 stop-loss hit" (APCd, 30 Jul 2026). No data means no decision.
        if (currentPrice <= 0)
            return Task.FromResult(new PositionCheckResult(
                ExitReason.None, currentPrice, null));

        // Step 1 — stop loss (highest priority)
        if (currentPrice <= trade.StopLossPrice)
            return Task.FromResult(new PositionCheckResult(
                ExitReason.StopLossHit, currentPrice, null));

        // Step 2 — target hit
        if (currentPrice >= trade.TargetPrice)
            return Task.FromResult(new PositionCheckResult(
                ExitReason.TargetHit, currentPrice, null));

        // Step 3 — trailing stop
        var activationThreshold = trade.EntryPrice * (1 + (decimal)trailingActivationPct);
        var trailingDistance = (decimal)trailingDistancePct;

        if (trade.TrailingStopPrice.HasValue)
        {
            // Already armed — update or trigger
            var newTrail = currentPrice * (1 - trailingDistance);

            if (currentPrice <= trade.TrailingStopPrice.Value)
                return Task.FromResult(new PositionCheckResult(
                    ExitReason.TrailingStopHit, currentPrice, null));

            if (newTrail > trade.TrailingStopPrice.Value)
                return Task.FromResult(new PositionCheckResult(
                    ExitReason.None, currentPrice, newTrail));
        }
        else if (currentPrice >= activationThreshold)
        {
            // Just crossed the activation threshold — arm the trail
            var newTrail = currentPrice * (1 - trailingDistance);
            return Task.FromResult(new PositionCheckResult(
                ExitReason.None, currentPrice, newTrail));
        }

        // Step 4 — hard time cap. maxHoldDays is now a SOFT guide-hold: a
        // position still showing healthy momentum past it keeps running (the
        // daily momentum check in ResearchConsumer exits stalled runners). This
        // is the absolute backstop - guide-hold x HoldCeilingMultiple - so a
        // runner can't be held forever even if momentum keeps flickering
        // healthy. Count TRADING days, not calendar days, so weekends/holidays
        // don't burn the budget when the market was shut.
        var daysHeld = marketCalendar.TradingDaysBetween(
            DateOnly.FromDateTime(trade.OpenedAt), DateOnly.FromDateTime(DateTime.UtcNow));
        var hardCeiling = (int)Math.Ceiling(maxHoldDays * CapitalRules.HoldCeilingMultiple);
        if (daysHeld > hardCeiling
            && currentPrice > trade.StopLossPrice
            && currentPrice < trade.TargetPrice)
        {
            return Task.FromResult(new PositionCheckResult(
                ExitReason.TimeExit, currentPrice, null));
        }

        // Step 5 — no exit
        return Task.FromResult(new PositionCheckResult(
            ExitReason.None, currentPrice, null));
    }
}
