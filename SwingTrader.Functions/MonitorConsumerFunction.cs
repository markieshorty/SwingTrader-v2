using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (monitor-jobs queue): checks
// open positions against stop/target/trailing-stop/time-exit rules and the
// portfolio circuit breaker. Never places orders itself - see MonitorService
// for why closing positions automatically is left as a manual, alerted action.
public class MonitorConsumerFunction(
    IJobLogRepository jobLog,
    IMonitorService monitor,
    IWorkerHeartbeatRepository heartbeats,
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
            await heartbeats.UpsertAsync("Monitor", "Success", summary);

            logger.LogInformation("Monitor job {JobId} for account {AccountId} — {Summary}", message.JobId, message.AccountId, summary);

            await jobLog.MarkCompletedAsync(message.AccountId, "Monitor", jobDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync("Monitor", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Monitor", jobDate, ex.Message, ct);
            throw;
        }
    }
}
