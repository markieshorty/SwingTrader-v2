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
    IAccountRepository accounts,
    IWatchlistRepository watchlists,
    IQualitativeWatchlistService qualitative,
    Agents.SecondHop.IEconomicLinkService economicLinks,
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

            var screenResult = await screener.ScreenAsync(message.AccountId, finnhub, ct);
            var candidates = screenResult.Candidates;

            // A large chunk of the universe failing its quote fetch together
            // (rate limiting, Finnhub outage) quietly shrinks the candidate
            // pool with no other visible signal - same concern Research
            // already surfaces via "N of M symbol(s) could not be rescored".
            if (screenResult.UniverseCount > 0 && screenResult.FailedQuoteCount > screenResult.UniverseCount * 0.2)
            {
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Screener Incomplete", "Warning",
                    $"{screenResult.FailedQuoteCount} of {screenResult.UniverseCount} universe symbol(s) failed their quote fetch this run " +
                    "— the candidate pool may be smaller than usual. Check Finnhub rate limits/availability.", ct);
            }

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

            var account = await accounts.GetAsync(message.AccountId, ct);
            var targetSize = account?.TargetWatchlistSize ?? Core.Constants.CapitalRules.DefaultTargetWatchlistSize;
            var selections = await selector.SelectAsync(claude, candidates, spyChange, vix, targetSize, ct);
            if (selections is null || selections.Count == 0)
            {
                logger.LogWarning("Watchlist selection returned empty for account {AccountId} — watchlist unchanged", message.AccountId);
                await heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Warning", "Selection returned empty — watchlist unchanged");
                await jobLog.MarkCompletedAsync(message.AccountId, "Watchlist", jobDate, ct);
                return;
            }

            var updateResult = await updater.UpdateAsync(message.AccountId, selections, ct);

            // Qualitative sibling list (docs/qualitative-watchlist-plan):
            // Claude picks over the whole universe on narrative grounds,
            // applied to the account's (created-disabled) AiQualitative
            // watchlist. Best-effort - a failed selection keeps last week's
            // picks and never fails the technical refresh.
            try
            {
                var qualitativeApplied = await qualitative.RefreshAsync(message.AccountId, claude, ct);
                if (qualitativeApplied > 0)
                    logger.LogInformation("Qualitative watchlist: {Count} pick(s) applied (account {AccountId})", qualitativeApplied, message.AccountId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Qualitative watchlist refresh failed (account {AccountId}) — retries next week", message.AccountId);
            }

            // Second-hop economic graph refresh rides this weekly job
            // (docs/second-hop-plan): rebuild links for whichever of the
            // account's post-update symbols are missing/stale. Best-effort -
            // a failed graph build never fails the watchlist refresh.
            try
            {
                var currentSymbols = (await watchlists.GetAllEnabledSymbolsAsync(message.AccountId, ct))
                    .Select(i => i.Symbol)
                    .ToList();
                var refreshed = await economicLinks.RefreshStaleLinksAsync(currentSymbols, ct);
                if (refreshed > 0)
                    logger.LogInformation("Economic-link graphs refreshed for {Count} symbol(s) (account {AccountId})", refreshed, message.AccountId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Economic-link refresh failed after watchlist update (account {AccountId}) — retries next week", message.AccountId);
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
