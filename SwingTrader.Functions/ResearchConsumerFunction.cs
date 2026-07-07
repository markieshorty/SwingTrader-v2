using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (research-jobs queue): scores
// every active watchlist symbol for the account and persists the resulting
// StockSignal rows.
public class ResearchConsumerFunction(
    IJobLogRepository jobLog,
    IWatchlistRepository watchlist,
    IResearchPipeline pipeline,
    IWorkerHeartbeatRepository heartbeats,
    IUserHttpClientFactory clientFactory,
    ILogger<ResearchConsumerFunction> logger)
{
    // Mirrors ResearchConfig.MaxConcurrentSymbols - kept as a literal here
    // rather than threading IOptions through the Functions host for one value.
    private const int MaxConcurrentSymbols = 3;

    [Function("ResearchConsumer")]
    public async Task Run(
        [ServiceBusTrigger("research-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ResearchJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Research", message.TradeDate, ct);

        try
        {
            // Deduplicated union of every enabled watchlist, not just the
            // default AI-managed one - a symbol on multiple enabled
            // watchlists is researched once.
            var symbols = await watchlist.GetAllEnabledSymbolsAsync(message.AccountId, ct);
            logger.LogInformation(
                "Research job {JobId} for account {AccountId} — scoring {Count} symbols",
                message.JobId, message.AccountId, symbols.Count);

            // One set of per-account HTTP clients reused across every symbol in this job -
            // there's no per-symbol credential difference, only per-account.
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(message.AccountId, ct);
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);

            using var semaphore = new SemaphoreSlim(MaxConcurrentSymbols);
            var tasks = symbols.Select(async s =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    await pipeline.RunAsync(message.AccountId, finnhub, tiingo, claude, s.Symbol, ct);
                }
                catch (Exception ex)
                {
                    // One symbol failing shouldn't fail the whole account's research run -
                    // the job as a whole still completes with partial signal coverage.
                    logger.LogWarning(ex, "Research failed for {Symbol} (account {AccountId})", s.Symbol, message.AccountId);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            await heartbeats.UpsertAsync(message.AccountId, "Research", "Success", $"{symbols.Count} symbol(s) scored");
            await jobLog.MarkCompletedAsync(message.AccountId, "Research", message.TradeDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Research", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Research", message.TradeDate, ex.Message, ct);
            throw; // Re-throw so Service Bus retries, then dead-letters after maxDeliveryCount.
        }
    }
}
