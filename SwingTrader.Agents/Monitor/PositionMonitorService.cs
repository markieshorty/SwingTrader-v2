using Microsoft.Extensions.Options;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Agents.Monitor;

public class PositionMonitorService(IOptions<MonitorConfig> config) : IPositionMonitorService
{
    private readonly MonitorConfig _cfg = config.Value;

    public Task<PositionCheckResult> CheckPositionAsync(
        Trade trade,
        decimal currentPrice,
        CancellationToken ct = default)
    {
        // Step 1 — stop loss (highest priority)
        if (currentPrice <= trade.StopLossPrice)
            return Task.FromResult(new PositionCheckResult(
                ExitReason.StopLossHit, currentPrice, null));

        // Step 2 — target hit
        if (currentPrice >= trade.TargetPrice)
            return Task.FromResult(new PositionCheckResult(
                ExitReason.TargetHit, currentPrice, null));

        // Step 3 — trailing stop
        var activationThreshold = trade.EntryPrice * (1 + (decimal)_cfg.TrailingActivationPct);
        var trailingDistance = (decimal)_cfg.TrailingDistancePct;

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

        // Step 4 — time exit (thesis stalled, neither stop nor target hit)
        var daysHeld = (int)(DateTime.UtcNow - trade.OpenedAt).TotalDays;
        if (daysHeld > _cfg.MaxHoldDays
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
