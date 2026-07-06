using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Monitor;

// Deliberately read-only with respect to the broker: this cycle updates
// trailing-stop bookkeeping (a DB field, not an order) and *flags* stop/
// target/time/circuit-breaker exits by email rather than calling
// t212.PlaceMarketOrderAsync to close them. Closing a real position
// automatically is the same category of financial risk as the (still
// deferred) Execution agent, and is intentionally left as a manual action
// pending a reviewed, explicitly-approved execution path.
public class MonitorService(
    ITradeRepository tradeRepo,
    IPortfolioRepository portfolioRepo,
    IPortfolioCircuitBreakerService circuitBreaker,
    IPositionMonitorService positionMonitor,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    ILogger<MonitorService> logger) : IMonitorService
{
    public async Task<MonitorCycleResult> RunCycleAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITrading212Client t212,
        CancellationToken ct = default)
    {
        // Fetch account/summary once per cycle and share it between the
        // circuit breaker check and the snapshot update - T212's rate limit
        // is tight enough that hitting this endpoint twice every 5-minute
        // cycle was a meaningful contributor to 429s.
        T212AccountSummary? summary = null;
        try
        {
            summary = await t212.GetAccountSummaryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch T212 account summary for account {AccountId} this cycle", accountId);
        }

        // Step 1 — circuit breaker check
        if (await circuitBreaker.ShouldTriggerAsync(accountId, summary, ct))
        {
            var openTrades = (await tradeRepo.GetOpenTradesAsync(accountId)).ToList();
            var flagged = openTrades.Select(t => new FlaggedExit(t.Symbol, ExitReason.CircuitBreaker, t.EntryPrice)).ToList();

            await SendAlertAsync(
                accountId,
                $"# \U0001F6A8 CIRCUIT BREAKER TRIGGERED\n\n" +
                $"The daily loss circuit breaker has fired for {openTrades.Count} open position(s): " +
                $"{string.Join(", ", openTrades.Select(t => t.Symbol))}.\n\n" +
                $"**No positions were closed automatically — review and close manually in Trading212.**",
                "\U0001F6A8 SwingTrader — CIRCUIT BREAKER TRIGGERED, manual review needed",
                NotificationCategory.CircuitBreaker);

            return new MonitorCycleResult(openTrades.Count, 0, flagged, true);
        }

        // Step 2 — check each position
        var trades = (await tradeRepo.GetOpenTradesAsync(accountId)).ToList();

        int checked_ = 0, trailingUpdated = 0;
        var flaggedExits = new List<FlaggedExit>();

        if (trades.Count == 0)
        {
            logger.LogDebug("No open positions to monitor for account {AccountId}", accountId);
        }

        foreach (var trade in trades)
        {
            if (ct.IsCancellationRequested)
            {
                logger.LogWarning("Shutdown requested mid-cycle — stopping before checking further positions");
                break;
            }
            try
            {
                var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                if (quote.CurrentPrice is null)
                {
                    logger.LogWarning("Finnhub returned no price for {Symbol} — will retry next cycle", trade.Symbol);
                    continue;
                }
                var currentPrice = quote.CurrentPrice.Value;

                var result = await positionMonitor.CheckPositionAsync(trade, currentPrice, ct);

                if (result.UpdatedTrailingStop.HasValue && result.Reason == ExitReason.None)
                {
                    trade.TrailingStopPrice = result.UpdatedTrailingStop.Value;
                    if (trade.Notes == null || !trade.Notes.Contains("TrailingActive"))
                        trade.Notes = (trade.Notes ?? string.Empty).TrimEnd() + " | TrailingActive";
                    await tradeRepo.UpdateAsync(trade);
                    logger.LogInformation("{Symbol}: trailing stop updated to ${Stop:F2}", trade.Symbol, result.UpdatedTrailingStop.Value);
                    trailingUpdated++;
                }
                else if (result.Reason != ExitReason.None)
                {
                    logger.LogInformation("{Symbol}: exit condition flagged — {Reason} at ${Price:F2}", trade.Symbol, result.Reason, currentPrice);
                    flaggedExits.Add(new FlaggedExit(trade.Symbol, result.Reason, currentPrice));
                }

                checked_++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check {Symbol} — will retry next cycle", trade.Symbol);
            }

            // Small delay between symbols to avoid Finnhub rate limits.
            // Skip on shutdown so the next loop iteration can break cleanly.
            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (flaggedExits.Count > 0)
        {
            var lines = flaggedExits.Select(f => $"- **{f.Symbol}**: {f.Reason} at ${f.CurrentPrice:F2}");
            await SendAlertAsync(
                accountId,
                $"# \U0001F4CD Position exit conditions met\n\n" +
                string.Join("\n", lines) +
                "\n\nThese positions have hit a stop/target/time exit condition and are ready to close — " +
                "review and close manually in Trading212.",
                $"SwingTrader — {flaggedExits.Count} position(s) ready to close",
                NotificationCategory.Execution);
        }

        // Step 3 — refresh snapshot every cycle so the portfolio API/report reads current values
        await UpdateSnapshotAsync(accountId, summary);

        return new MonitorCycleResult(checked_, trailingUpdated, flaggedExits, false);
    }

    private async Task SendAlertAsync(int accountId, string markdown, string subject, NotificationCategory category)
    {
        try
        {
            var toAddresses = (await recipients.ListAsync(accountId))
                .Where(r => r.Categories.HasFlag(category))
                .Select(r => r.Email)
                .ToList();

            if (toAddresses.Count > 0)
                await emailService.SendSimpleEmailAsync(toAddresses, markdown, subject);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send monitor alert email for account {AccountId}", accountId);
        }
    }

    private async Task UpdateSnapshotAsync(int accountId, T212AccountSummary? summary)
    {
        if (summary is null)
        {
            logger.LogWarning("No account summary available — skipping portfolio snapshot update for account {AccountId}", accountId);
            return;
        }

        try
        {
            // TotalValue/Investments.CurrentValue are already in the
            // account's base currency (GBP) - T212 computes these itself.
            var totalValue = summary.TotalValue;
            var openValue = summary.Investments.CurrentValue;

            await portfolioRepo.AddAsync(new PortfolioSnapshot
            {
                AccountId = accountId,
                SnapshotDate = DateOnly.FromDateTime(DateTime.UtcNow),
                TotalCapital = totalValue,
                CashAvailable = summary.Cash.AvailableToTrade,
                OpenPositionsValue = openValue,
                ActiveCapital = 0m,
                LockedCapital = 0m,
                ReserveCapital = 0m,
                TotalPnl = 0m,
                CurrentTier = Core.Enums.CapitalTier.Tier1,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update portfolio snapshot after monitor cycle for account {AccountId}", accountId);
        }
    }
}
