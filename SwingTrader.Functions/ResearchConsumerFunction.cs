using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Monitor;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
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
    ITradeRepository tradeRepo,
    IAccountRiskProfileRepository riskProfileRepo,
    IMomentumHealthService momentumHealth,
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

            // Momentum health check — runs once per account after every symbol
            // has a fresh signal for today. See MomentumHealthCheck_ProbationPhase.md.
            await CheckOpenPositionHealthAsync(message.AccountId, ct);

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

    // Two-phase lifecycle: a position is checked exactly once on day
    // MinHoldDays, with one grace day if the verdict is Borderline. Confirmed
    // positions are never rechecked — the thesis has been validated and runs
    // to MaxHoldDays under the normal stop/target/trailing exit rules.
    private async Task CheckOpenPositionHealthAsync(int accountId, CancellationToken ct)
    {
        var profile = await riskProfileRepo.GetAsync(accountId, ct);
        var openTrades = (await tradeRepo.GetOpenTradesAsync(accountId)).ToList();
        if (openTrades.Count == 0) return;

        var today = DateTime.UtcNow;

        foreach (var trade in openTrades)
        {
            if (trade.Phase != TradePhase.Probation)
                continue; // Confirmed positions aren't rechecked; Exiting positions are awaiting close.

            var daysHeld = (today - trade.OpenedAt).Days;
            var isGraceDayRecheck = daysHeld == profile.MinHoldDays + 1 && trade.MomentumHealthVerdict == "Borderline";
            if (daysHeld != profile.MinHoldDays && !isGraceDayRecheck)
                continue;

            var result = await momentumHealth.CalculateAsync(accountId, trade, ct);

            // One grace day is enough — a still-Borderline verdict on the
            // recheck day is treated the same as a fresh Exit rather than
            // extending probation indefinitely.
            var verdict = isGraceDayRecheck && result.Verdict == "Borderline" ? "Exit" : result.Verdict;

            trade.MomentumHealthScore = result.Score;
            trade.MomentumHealthVerdict = verdict;
            trade.MomentumHealthReasoning = result.Reasoning;
            trade.MomentumHealthCheckedAt = DateTime.UtcNow;

            if (verdict == "Confirmed")
            {
                trade.Phase = TradePhase.Confirmed;
                trade.PhaseConfirmedAt = DateTime.UtcNow;
                logger.LogInformation(
                    "{Symbol} (account {AccountId}) momentum confirmed (score {Score:F2}) — letting run to day {Max}",
                    trade.Symbol, accountId, result.Score, profile.MaxHoldDays);
            }
            else if (verdict == "Exit")
            {
                trade.Phase = TradePhase.Exiting;
                logger.LogInformation(
                    "{Symbol} (account {AccountId}) failing momentum check (score {Score:F2}) — queued for automatic exit",
                    trade.Symbol, accountId, result.Score);
            }
            else
            {
                logger.LogInformation(
                    "{Symbol} (account {AccountId}) borderline momentum (score {Score:F2}) — one more day",
                    trade.Symbol, accountId, result.Score);
            }

            await tradeRepo.UpdateAsync(trade);
        }
    }
}
