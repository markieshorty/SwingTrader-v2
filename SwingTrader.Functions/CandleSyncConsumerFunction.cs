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
    ISentimentArchiveRepository sentimentArchive,
    Microsoft.Extensions.Options.IOptions<Infrastructure.Configuration.ResearchConfig> researchConfig,
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

            // Sentiment-archive retention piggybacks this weekly platform job:
            // article METADATA older than ArchiveRetentionMonths is pruned;
            // daily scores are never touched (they're the point of the
            // archive). Best-effort - a prune failure never fails the sync.
            try
            {
                var cutoff = DateOnly.FromDateTime(
                    DateTime.UtcNow.AddMonths(-researchConfig.Value.ArchiveRetentionMonths));
                var pruned = await sentimentArchive.PruneArticlesAsync(cutoff, ct);
                if (pruned > 0)
                    logger.LogInformation("Sentiment archive: pruned {Count} article rows older than {Cutoff}", pruned, cutoff);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sentiment-archive prune failed — will retry on the next weekly sync");
            }
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "CandleSync", "Failed", ex.Message);
            throw;
        }
    }
}
