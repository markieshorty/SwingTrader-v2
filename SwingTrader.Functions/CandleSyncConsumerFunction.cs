using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Platform-level job (one run refreshes the shared HistoricalCandles table for
// every account) - enqueued weekly by the Scheduler under the system account,
// or manually from the Strategy Lab. Uses the platform Tiingo key, never
// per-user keys.
public class CandleSyncConsumerFunction(
    ICandleSyncService candleSync,
    IWorkerHeartbeatRepository heartbeats,
    IActivityLogRepository activityLog,
    ILogger<CandleSyncConsumerFunction> logger)
{
    [Function("CandleSyncConsumer")]
    public async Task Run(
        [ServiceBusTrigger("candlesync-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<CandleSyncJobMessage>(messageBody)!;

        try
        {
            var result = await candleSync.SyncAsync(ct);

            var status = !result.Configured ? "Warning" : result.SymbolsFailed > result.SymbolsSynced ? "Warning" : "Success";
            await heartbeats.UpsertAsync(message.AccountId, "CandleSync", status, result.Summary);
            await activityLog.LogAsync(message.AccountId, "WorkerRun", "Candle Sync", status == "Success" ? "Info" : "Warning", result.Summary, ct);
            logger.LogInformation("CandleSync job {JobId} — {Summary}", message.JobId, result.Summary);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "CandleSync", "Failed", ex.Message);
            throw;
        }
    }
}
