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

        // Qualitative-only repair run: no screening, no selection, no job-log
        // involvement (so it can never collide with, or block, the weekly
        // full run). Refreshes the AI picks and leaves everything else alone.
        if (message.QualitativeOnly == true)
        {
            try
            {
                var qClaude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);
                var result = await qualitative.RefreshAsync(message.AccountId, qClaude, ct);
                if (result.Applied > 0)
                    await activityLog.LogAsync(message.AccountId, "WorkerRun", "Qualitative Watchlist", "Success",
                        $"{result.Applied} qualitative pick(s) refreshed (qualitative-only run).", ct);
                else
                    await activityLog.LogAsync(message.AccountId, "WorkerRun", "Qualitative Watchlist", "Warning",
                        $"Qualitative-only refresh did not apply: {result.Failure ?? "feature disabled"}", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Qualitative-only refresh failed (account {AccountId})", message.AccountId);
                await activityLog.LogAsync(message.AccountId, "WorkerRun", "Qualitative Watchlist", "Warning",
                    $"Qualitative-only refresh failed ({ex.GetType().Name}) — previous picks stand.", ct);
            }
            return;
        }

        // Duplicate-run guard (20 Jul 2026): overlapping deliveries (a queue
        // redelivery racing a manual/admin invocation) used to run the same
        // account's selection twice, back to back, bursting the Claude API.
        // A RECENT Processing entry means another run is genuinely in flight -
        // skip. A stale one (crashed host) falls through and re-runs.
        var existing = await jobLog.FindAsync(message.AccountId, "Watchlist", jobDate, ct);
        if (existing is { Status: Core.Enums.JobStatus.Processing }
            && existing.UpdatedAt > DateTime.UtcNow.AddMinutes(-90))
        {
            logger.LogInformation(
                "Watchlist job for account {AccountId} on {JobDate} is already processing (since {UpdatedAt:u}) — duplicate delivery skipped",
                message.AccountId, jobDate, existing.UpdatedAt);
            return;
        }

        await jobLog.MarkProcessingAsync(message.AccountId, "Watchlist", jobDate, ct);
        await activityLog.LogAsync(message.AccountId, "WorkerRun", "Watchlist", "Started", "Screening universe and selecting this week's watchlist…", ct);

        try
        {
            // Stage breadcrumbs (20 Jul 2026): a refresh is 10-15 externally
            // rate-limited minutes, and "slow" was indistinguishable from
            // "stuck". Each stage updates the Watchlist heartbeat, which the
            // toolbar job chip surfaces live - a failure names its stage.
            Task StageAsync(string text) =>
                heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Running", text);

            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);

            await StageAsync("1/5 Screening the universe (quote sweep + liquidity floor)…");
            var screenResult = await screener.ScreenAsync(message.AccountId, finnhub, ct);
            var candidates = screenResult.Candidates;
            await StageAsync($"2/5 Screened {screenResult.UniverseCount} symbols → {candidates.Count} candidates. Pausing for rate budget…");

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
            await StageAsync($"3/5 Claude selecting {targetSize} from {candidates.Count} candidates…");
            var selections = await selector.SelectAsync(claude, candidates, spyChange, vix, targetSize, ct);
            if (selections is null || selections.Count == 0)
            {
                logger.LogWarning("Watchlist selection returned empty for account {AccountId} — watchlist unchanged", message.AccountId);
                await heartbeats.UpsertAsync(message.AccountId, "Watchlist", "Warning", "Selection returned empty — watchlist unchanged");
                await jobLog.MarkCompletedAsync(message.AccountId, "Watchlist", jobDate, ct);
                return;
            }

            var updateResult = await updater.UpdateAsync(message.AccountId, selections, ct);
            await StageAsync($"4/5 Applied: {updateResult.Added} added, {updateResult.Removed} removed, {updateResult.Kept} kept. Qualitative picks next…");

            // Qualitative sibling list (docs/qualitative-watchlist-plan):
            // Claude picks over the whole universe on narrative grounds,
            // applied to the account's (created-disabled) AiQualitative
            // watchlist. Best-effort - a failed selection keeps last week's
            // picks and never fails the technical refresh.
            try
            {
                var qualitativeResult = await qualitative.RefreshAsync(message.AccountId, claude, ct);
                if (qualitativeResult.Applied > 0)
                {
                    logger.LogInformation("Qualitative watchlist: {Count} pick(s) applied (account {AccountId})", qualitativeResult.Applied, message.AccountId);
                    await activityLog.LogAsync(message.AccountId, "WorkerRun", "Qualitative Watchlist", "Success",
                        $"{qualitativeResult.Applied} qualitative pick(s) refreshed.", ct);
                }
                else if (qualitativeResult.Failure is not null)
                {
                    // Durable breadcrumb: telemetry proved unreliable on the
                    // per-function-scaled instances (20 Jul incident), so a
                    // failed refresh must be visible in the dashboard's
                    // activity log, not just a maybe-ingested warning.
                    await activityLog.LogAsync(message.AccountId, "WorkerRun", "Qualitative Watchlist", "Warning",
                        $"Qualitative refresh did not apply: {qualitativeResult.Failure}", ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Qualitative watchlist refresh failed (account {AccountId}) — retries next week", message.AccountId);
                await activityLog.LogAsync(message.AccountId, "WorkerRun", "Qualitative Watchlist", "Warning",
                    $"Qualitative refresh failed ({ex.GetType().Name}) — previous picks stand, retries next week.", ct);
            }

            await StageAsync("5/5 Refreshing second-hop economic links…");

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
