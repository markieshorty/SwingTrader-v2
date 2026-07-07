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
        var jobDate = DateOnly.FromDateTime(message.CycleTime);
        await jobLog.MarkProcessingAsync(message.AccountId, "Monitor", jobDate, ct);

        try
        {
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var t212 = await clientFactory.CreateTrading212Async<ITrading212Client>(message.AccountId, ct);

            var result = await monitor.RunCycleAsync(message.AccountId, finnhub, t212, ct);

            var summary = result.CircuitBreakerTriggered
                ? "Circuit breaker triggered — manual review required"
                : $"{result.PositionsChecked} checked, {result.TrailingStopsUpdated} trailing stops updated, {result.FlaggedExits.Count} exit(s) flagged";
            await heartbeats.UpsertAsync(message.AccountId, "Monitor", "Success", summary);

            if (result.CircuitBreakerTriggered)
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Circuit Breaker", "Warning",
                    "Portfolio circuit breaker triggered — manual review required", ct);

            foreach (var exit in result.FlaggedExits)
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Exit Signal", "Warning",
                    $"{exit.Symbol} — {exit.Reason} at £{exit.CurrentPrice:F2}", ct);

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
