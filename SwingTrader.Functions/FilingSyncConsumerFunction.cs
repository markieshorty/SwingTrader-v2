using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Filings;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Platform-level job (docs/filing-delta-plan): one run refreshes the shared
// Filings/FilingDeltas tables for every account - enqueued daily by the
// Scheduler under the system account. EDGAR is free; Claude runs only on
// filings whose language actually changed (the hash gate).
public class FilingSyncConsumerFunction(
    IFilingSyncService filingSync,
    IWorkerHeartbeatRepository heartbeats,
    IActivityLogRepository activityLog,
    ILogger<FilingSyncConsumerFunction> logger)
{
    [Function("FilingSyncConsumer")]
    public async Task Run(
        [ServiceBusTrigger("filingsync-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<FilingSyncJobMessage>(messageBody)!;

        try
        {
            var result = await filingSync.SyncAsync(ct);

            var status = !result.Configured ? "Warning" : result.Failed > 0 ? "Warning" : "Success";
            await heartbeats.UpsertAsync(message.AccountId, "FilingSync", status, result.Summary);
            await activityLog.LogAsync(message.AccountId, "WorkerRun", "Filing Sync",
                status == "Success" ? "Info" : "Warning", result.Summary, ct);
            logger.LogInformation("FilingSync job {JobId} — {Summary}", message.JobId, result.Summary);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "FilingSync", "Failed", ex.Message);
            throw;
        }
    }
}
