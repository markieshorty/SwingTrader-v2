using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Watchlist;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (watchlist-jobs queue): screens
// the stock universe, has Claude pick this week's watchlist, and applies the
// diff (respecting open positions) for the account.
public class WatchlistConsumerFunction(
    IJobLogRepository jobLog,
    IStockScreener screener,
    IWatchlistSelectionService selector,
    IWatchlistUpdateService updater,
    IAccountRiskProfileRepository riskProfileRepo,
    IWorkerHeartbeatRepository heartbeats,
    IActivityLogRepository activityLog,
    IUserHttpClientFactory clientFactory,
    ILogger<WatchlistConsumerFunction> logger)
{
    [Function("WatchlistConsumer")]
    public async Task Run(
        [ServiceBusTrigger("watchlist-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<WatchlistJobMessage>(messageBody)!;
        var jobDate = DateOnly.FromDateTime(message.ScheduledFor);
        await jobLog.MarkProcessingAsync(message.AccountId, "Watchlist", jobDate, ct);
        await activityLog.LogAsync(message.AccountId, "WorkerRun", "Watchlist", "Started", "Screening universe and selecting this week's watchlist…", ct);

        try
        {
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);

            var candidates = await screener.ScreenAsync(message.AccountId, finnhub, ct);
            if (candidates.Count < 10)
            {
                logger.LogWarning(
                    "Insufficient candidates ({Count}) for account {AccountId} — watchlist unchanged",
                    candidates.Count, message.AccountId);
                await heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Warning", $"Insufficient candidates ({candidates.Count}) — watchlist unchanged");
                await jobLog.MarkCompletedAsync(message.AccountId, "Watchlist", jobDate, ct);
                return;
            }

            // Screener already burns most of the per-minute rate budget across the
            // whole universe - a brief pause here avoids 429s on the SPY/VIX calls.
            await Task.Delay(TimeSpan.FromSeconds(65), ct);

            decimal spyChange = 0m, vix = 20m;
            try
            {
                var spy = await finnhub.GetQuoteAsync("SPY");
                spyChange = spy.PercentChange ?? 0m;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch SPY quote — using 0%");
            }

            try
            {
                var vixQuote = await finnhub.GetQuoteAsync("VIX");
                vix = vixQuote.CurrentPrice ?? 20m;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch VIX — using 20");
            }

            var riskProfile = await riskProfileRepo.GetAsync(message.AccountId, ct);
            var selections = await selector.SelectAsync(claude, candidates, spyChange, vix, riskProfile.TargetWatchlistSize, ct);
            if (selections is null || selections.Count == 0)
            {
                logger.LogWarning("Watchlist selection returned empty for account {AccountId} — watchlist unchanged", message.AccountId);
                await heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Warning", "Selection returned empty — watchlist unchanged");
                await jobLog.MarkCompletedAsync(message.AccountId, "Watchlist", jobDate, ct);
                return;
            }

            var updateResult = await updater.UpdateAsync(message.AccountId, selections, ct);

            if (updateResult.SkippedForCapacity.Count > 0)
            {
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Watchlist Capacity", "Warning",
                    $"{updateResult.SkippedForCapacity.Count} selection(s) skipped this refresh — adding them would have " +
                    $"exceeded the 100-symbol enabled-watchlist limit: {string.Join(", ", updateResult.SkippedForCapacity)}. " +
                    "Disable another watchlist or remove some symbols to make room.", ct);
            }

            await heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Success", $"{selections.Count} symbols selected from {candidates.Count} candidates");
            await jobLog.MarkCompletedAsync(message.AccountId, "Watchlist", jobDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Watchlist", jobDate, ex.Message, ct);
            throw;
        }
    }
}
