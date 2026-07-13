using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.SecondHop;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Platform-level daily job (docs/second-hop-plan): scores the bellwether
// set's news into the shared sentiment archive before research runs, so the
// second-hop relevance pass has fresh source events. Uses platform keys.
public class BellwetherSyncConsumerFunction(
    IBellwetherSyncService bellwetherSync,
    IWorkerHeartbeatRepository heartbeats,
    IActivityLogRepository activityLog,
    ILogger<BellwetherSyncConsumerFunction> logger)
{
    [Function("BellwetherSyncConsumer")]
    public async Task Run(
        [ServiceBusTrigger("bellwether-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<BellwetherSyncJobMessage>(messageBody)!;

        try
        {
            var result = await bellwetherSync.SyncAsync(ct);

            var status = !result.Configured ? "Warning" : result.Failed > result.Scored ? "Warning" : "Success";
            await heartbeats.UpsertAsync(message.AccountId, "BellwetherSync", status, result.Summary);
            await activityLog.LogAsync(message.AccountId, "WorkerRun", "Bellwether Sync",
                status == "Success" ? "Info" : "Warning", result.Summary, ct);
            logger.LogInformation("BellwetherSync job {JobId} — {Summary}", message.JobId, result.Summary);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "BellwetherSync", "Failed", ex.Message);
            throw;
        }
    }
}
