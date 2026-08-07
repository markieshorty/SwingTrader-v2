using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
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
    SwingTrader.Agents.Backtesting.IDelistedBackfillService delistedBackfill,
    SwingTrader.Infrastructure.Storage.ICandleBlobMigrationService blobMigration,
    IServiceProvider services,
    IJobLogRepository jobLog,
    Azure.Messaging.ServiceBus.ServiceBusClient? serviceBus,
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

        // Small-cap filing-event scan (docs/filing-events-plan P1): once per
        // trading evening; observation only.
        if (string.Equals(message.Mode, "filingevents", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Filing events job {JobId} starting for account {AccountId}",
                message.JobId, message.AccountId);
            // The scheduler claims a job-log row before sending; nothing was
            // closing it, so every run left a permanent "Queued" capsule on
            // the dashboard (6 Aug). Mark* no-ops when there is no row, so
            // the manual trigger is unaffected.
            var jobDate = DateOnly.FromDateTime(DateTime.UtcNow);
            await jobLog.MarkProcessingAsync(message.AccountId, "FilingEvents", jobDate, ct);
            try
            {
                var filingEvents = services.GetRequiredService<SwingTrader.Agents.FilingEvents.IFilingEventScanService>();
                var scan = await filingEvents.ScanAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);
                await activityLog.LogAsync(message.AccountId, "WorkerRun", "Filing Events",
                    !scan.Enabled ? "Skipped" : scan.Failed > 0 ? "Warning" : "Info", scan.Summary, ct);
                logger.LogInformation("Filing events job {JobId} — {Summary}", message.JobId, scan.Summary);
                await jobLog.MarkCompletedAsync(message.AccountId, "FilingEvents", jobDate, ct);
            }
            catch (Exception ex)
            {
                // Logger FIRST - it needs no database and no cancellation
                // token, so the error survives even when the DB writes below
                // cannot run (7 Aug 2026: the real error was invisible for
                // hours because the catch block's own writes were failing).
                logger.LogError(ex, "Filing events job {JobId} failed: {Error}", message.JobId, ex.Message);
                try
                {
                    // CancellationToken.None deliberately: a cancelled token
                    // is one of the ways the error path can silently vanish.
                    await activityLog.LogAsync(message.AccountId, "WorkerRun", "Filing Events", "Failed",
                        ex.Message.Length > 900 ? ex.Message[..900] : ex.Message, CancellationToken.None);
                    await jobLog.MarkFailedAsync(message.AccountId, "FilingEvents", jobDate, ex.Message, CancellationToken.None);
                }
                catch (Exception logEx)
                {
                    logger.LogError(logEx, "Filing events job {JobId}: failed to RECORD the failure", message.JobId);
                }
                // Deliberately NOT rethrowing. This is an observation-only
                // job; retrying it ten times and dead-lettering poisons the
                // queue that the weekly candle sync, delisted backfill and
                // blob migration all share. A missed night costs nothing.
            }
            return;
        }

        // One-off SQL -> blob candle migration (docs/blob-candles-plan). Same
        // chunked-continuation shape as the delisted backfill below; the
        // completion summary carries the SQL-vs-blob count comparison that
        // gates the HistoricStore:UseBlob flip.
        if (string.Equals(message.Mode, "blobmigrate", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var mig = await blobMigration.RunChunkAsync(ct);
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Candle Blob Migration",
                    mig.Remaining > 0 ? "Info" : "Success", mig.Summary, ct);
                logger.LogInformation("Blob migration job {JobId} — {Summary}", message.JobId, mig.Summary);
                if (mig.Remaining > 0 && serviceBus is not null)
                {
                    await using var sender = serviceBus.CreateSender("candlesync-jobs");
                    await sender.SendMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage(
                        JsonSerializer.Serialize(new CandleSyncJobMessage(
                            message.AccountId, Guid.NewGuid().ToString("N"), "blobmigrate"))), ct);
                }
            }
            catch (Exception ex)
            {
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Candle Blob Migration", "Failed", ex.Message, ct);
                logger.LogError(ex, "Blob migration job {JobId} failed", message.JobId);
                throw;
            }
            return;
        }

        // Survivorship backfill mode (docs/survivorship-plan P1): one-shot
        // delisted-universe load, size-gated inside the service. The weekly
        // incremental sync is the default (Mode null).
        if (string.Equals(message.Mode, "delisted", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var backfill = await delistedBackfill.RunAsync(dryRun: false, ct);
                await heartbeats.UpsertAsync(message.AccountId, "CandleSync",
                    backfill.SizeGateBlocked ? "Failed" : "Success", backfill.Summary);
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Delisted Backfill",
                    backfill.SizeGateBlocked ? "Warning" : "Info", backfill.Summary, ct);
                logger.LogInformation("Delisted backfill job {JobId} — {Summary}", message.JobId, backfill.Summary);

                // Chunked run: enqueue the continuation as a FRESH message so
                // delivery counts reset per chunk and a mid-run host restart
                // only ever costs the current chunk.
                if (backfill.RemainingCandidates > 0 && serviceBus is not null)
                {
                    await using var sender = serviceBus.CreateSender("candlesync-jobs");
                    await sender.SendMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage(
                        JsonSerializer.Serialize(new CandleSyncJobMessage(
                            message.AccountId, Guid.NewGuid().ToString("N"), "delisted"))), ct);
                    logger.LogInformation("Delisted backfill continuation enqueued — {Remaining} candidate(s) to go",
                        backfill.RemainingCandidates);
                }
            }
            catch (Exception ex)
            {
                await heartbeats.UpsertAsync(message.AccountId, "CandleSync", "Failed", ex.Message);
                logger.LogError(ex, "Delisted backfill job {JobId} failed", message.JobId);
                throw;
            }
            return;
        }


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
