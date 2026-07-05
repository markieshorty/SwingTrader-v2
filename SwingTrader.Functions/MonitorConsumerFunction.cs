using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

public class MonitorConsumerFunction(IJobLogRepository jobLog, ILogger<MonitorConsumerFunction> logger)
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
            logger.LogInformation(
                "Monitor job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Monitor", jobDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Monitor", jobDate, ex.Message, ct);
            throw;
        }
    }
}
