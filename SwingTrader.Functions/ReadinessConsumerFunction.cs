using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Readiness;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (readiness-jobs queue): runs
// the readiness assessment once a day and persists a ReadinessSnapshot, so
// the readiness page's trajectory chart accumulates day-over-day history.
// Reads only (no external APIs, no orders) - purely a DB snapshot of metrics
// derived from existing trades/signals.
public class ReadinessConsumerFunction(
    IJobLogRepository jobLog,
    IReadinessAssessmentService readiness,
    IWorkerHeartbeatRepository heartbeats,
    ILogger<ReadinessConsumerFunction> logger)
{
    [Function("ReadinessConsumer")]
    public async Task Run(
        [ServiceBusTrigger("readiness-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ReadinessJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Readiness", message.SnapshotDate, ct);

        try
        {
            await readiness.RecordSnapshotAsync(message.AccountId, ct);

            logger.LogInformation(
                "Readiness job {JobId} for account {AccountId} — snapshot recorded for {Date}",
                message.JobId, message.AccountId, message.SnapshotDate);

            await heartbeats.UpsertAsync(message.AccountId, "Readiness", "Success", $"Snapshot recorded for {message.SnapshotDate}");
            await jobLog.MarkCompletedAsync(message.AccountId, "Readiness", message.SnapshotDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Readiness", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Readiness", message.SnapshotDate, ex.Message, ct);
            throw;
        }
    }
}
