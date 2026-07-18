using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

public class MonitorConsumerFunction(
    IJobLogRepository jobLog,
    IMonitorService monitor,
    IWorkerHeartbeatRepository heartbeats,
    IActivityLogRepository activityLog,
    IUserHttpClientFactory clientFactory,
    ILogger<MonitorConsumerFunction> logger)
{
    [Function("MonitorConsumer")]
    public async Task Run(
        [ServiceBusTrigger("monitor-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<MonitorJobMessage>(messageBody)!;

        // Off-hours slimline variant: refresh the T212 balance snapshot and
        // nothing else. No job log / heartbeat churn - it runs hourly around
        // the clock, and an account without a T212 key just skips quietly.
        if (message.BalanceOnly)
        {
            try
            {
                var t212Only = await clientFactory.CreateTrading212Async<ITrading212Client>(message.AccountId, ct);
                await monitor.SyncBalanceAsync(message.AccountId, t212Only, ct);
            }
            catch (Exception ex)
            {
                // Warning, not Debug: at Debug this was invisible in App
                // Insights, and a new account with a bad/missing T212 key
                // showing no balance was undiagnosable.
                logger.LogWarning(ex, "Balance-only sync failed for account {AccountId} (no key or T212 unavailable)", message.AccountId);
            }
            return;
        }

        var jobDate = DateOnly.FromDateTime(message.CycleTime);
        await jobLog.MarkProcessingAsync(message.AccountId, "Monitor", jobDate, ct);

        try
        {
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var t212 = await clientFactory.CreateTrading212Async<ITrading212Client>(message.AccountId, ct);

            // Tiingo feeds the bear-market regime check (SPY history). A user
            // without a Tiingo key just skips that step rather than failing
            // the whole monitor cycle.
            ITiingoClient? tiingo = null;
            try { tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(message.AccountId, ct); }
            catch { /* no key - bear check skipped */ }

            var result = await monitor.RunCycleAsync(message.AccountId, finnhub, t212, tiingo, ct);

            var executedExits = result.ExecutedExits ?? [];
            var summary = result.CircuitBreakerTriggered
                ? "Circuit breaker triggered — manual review required"
                : $"{result.PositionsChecked} checked, {result.TrailingStopsUpdated} trailing stops updated, " +
                  $"{executedExits.Count} exit(s) closed automatically, {result.FlaggedExits.Count} auto-close failure(s)";
            await heartbeats.UpsertAsync(message.AccountId, "Monitor", "Success", summary);

            if (result.CircuitBreakerTriggered)
            {
                var positions = result.FlaggedExits.Select(e => e.Symbol).Distinct().ToList();
                var positionList = positions.Count > 0 ? $" — positions: {string.Join(", ", positions)}" : "";
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Circuit Breaker", "Warning",
                    $"Portfolio circuit breaker triggered{positionList} — manual review required", ct);
            }

            // Only reached now when ClosePositionAsync genuinely failed (ticker
            // resolution or the T212 order itself) - a routine hit is handled by
            // executedExits below instead.
            foreach (var exit in result.FlaggedExits)
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Auto-Close Failed", "Warning",
                    $"{exit.Symbol} — {exit.Reason} at £{exit.CurrentPrice:F2} — order failed, will retry next cycle", ct);

            foreach (var exit in executedExits)
            {
                var pnlText = exit.RealizedPnl.HasValue ? $" (P&L £{exit.RealizedPnl:F2})" : "";
                var reasonText = exit.Reason switch
                {
                    ExitReason.MomentumHealthExit => "did not pass probation, selling early",
                    ExitReason.StopLossHit => "hit its stop loss",
                    ExitReason.TargetHit => "reached its target",
                    ExitReason.TrailingStopHit => "hit its trailing stop",
                    ExitReason.TimeExit => "reached its maximum hold period",
                    _ => "hit an exit condition",
                };
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Position Closed", "Info",
                    $"{exit.Symbol} {reasonText} — closed automatically at £{exit.ExitPrice:F2}{pnlText}", ct);
            }

            logger.LogInformation("Monitor job {JobId} for account {AccountId} — {Summary}", message.JobId, message.AccountId, summary);

            await jobLog.MarkCompletedAsync(message.AccountId, "Monitor", jobDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Monitor", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Monitor", jobDate, ex.Message, ct);
            throw;
        }
    }
}
