using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Monitor;

// Auto-closes every per-position exit (stop loss, target, trailing stop,
// time exit, momentum health) via IPositionExitService - a real T212 market
// sell, not just a flag. CircuitBreaker is the one exception: a portfolio-
// wide liquidation event stays flag-only (see Step 1 below) rather than
// auto-selling every open position across the whole account at once.
public class MonitorService(
    ITradeRepository tradeRepo,
    IPortfolioRepository portfolioRepo,
    IPortfolioCircuitBreakerService circuitBreaker,
    IPositionMonitorService positionMonitor,
    IAccountRiskProfileRepository riskProfileRepo,
    IPositionExitService positionExit,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    IOptions<ExecutionConfig> executionConfig,
    ILogger<MonitorService> logger) : IMonitorService
{
    private readonly ExecutionConfig _execution = executionConfig.Value;


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

        // Step 0 — reconcile any order placed last cycle (or earlier) whose
        // real fill price T212 hadn't confirmed yet. Runs before the circuit
        // breaker check so RealizedPnl/EntryPrice/ExitPrice are corrected as
        // early as possible once T212 confirms.
        await ReconcileOrderFillsAsync(accountId, t212, ct);

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
        var riskProfile = await riskProfileRepo.GetAsync(accountId, ct);
        var trades = (await tradeRepo.GetOpenTradesAsync(accountId)).ToList();

        int checked_ = 0, trailingUpdated = 0;
        var flaggedExits = new List<FlaggedExit>();
        var executedExits = new List<ExecutedExit>();

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

                // Momentum health exit takes priority over the normal stop/target/
                // trailing/time checks below — the Research Pipeline already decided
                // this position failed probation. Attempt the close every cycle until
                // it succeeds (network/broker errors just retry next cycle).
                if (trade.Phase == TradePhase.Exiting)
                {
                    var momentumExitResult = await positionExit.ClosePositionAsync(
                        accountId, trade, t212, currentPrice,
                        ExitReason.MomentumHealthExit, trade.MomentumHealthReasoning ?? "Momentum health check failed", ct);

                    if (momentumExitResult.Success)
                    {
                        executedExits.Add(new ExecutedExit(trade.Symbol, ExitReason.MomentumHealthExit, momentumExitResult.ExitPrice!.Value, momentumExitResult.RealizedPnl));
                    }
                    else
                    {
                        logger.LogWarning("{Symbol}: momentum health exit order failed — {Error}. Will retry next cycle.", trade.Symbol, momentumExitResult.ErrorMessage);
                    }

                    checked_++;
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                var result = await positionMonitor.CheckPositionAsync(
                    trade, currentPrice,
                    riskProfile.MaxHoldDays,
                    riskProfile.TrailingActivationPct,
                    riskProfile.TrailingDistancePct,
                    ct);

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
                    var reasonDetail = ExitReasonDetail(result.Reason, trade, currentPrice);
                    var exitResult = await positionExit.ClosePositionAsync(
                        accountId, trade, t212, currentPrice, result.Reason, reasonDetail, ct);

                    if (exitResult.Success)
                    {
                        executedExits.Add(new ExecutedExit(trade.Symbol, result.Reason, exitResult.ExitPrice!.Value, exitResult.RealizedPnl));
                    }
                    else
                    {
                        logger.LogWarning("{Symbol}: {Reason} exit order failed — {Error}. Will retry next cycle.", trade.Symbol, result.Reason, exitResult.ErrorMessage);
                        flaggedExits.Add(new FlaggedExit(trade.Symbol, result.Reason, currentPrice));
                    }
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

        // flaggedExits is only populated now when an auto-close attempt genuinely
        // failed (ticker resolution or the T212 order itself) - a routine hit no
        // longer lands here, since ClosePositionAsync handles it automatically.
        if (flaggedExits.Count > 0)
        {
            var lines = flaggedExits.Select(f => $"- **{f.Symbol}**: {f.Reason} at ${f.CurrentPrice:F2}");
            await SendAlertAsync(
                accountId,
                $"# ⚠️ Automatic close failed — action needed\n\n" +
                string.Join("\n", lines) +
                "\n\nSwingTrader tried to close these positions automatically but the order failed " +
                "(see the activity log for details). It will retry next cycle, but you may want to " +
                "close them manually in Trading212 if this persists.",
                $"SwingTrader — {flaggedExits.Count} position(s) failed to auto-close",
                NotificationCategory.Execution);
        }

        // Step 3 — refresh snapshot every cycle so the portfolio API/report reads current values
        await UpdateSnapshotAsync(accountId, summary);

        // Executed exits already send their own per-position email from
        // PositionExitService — no separate summary email needed here.
        return new MonitorCycleResult(checked_, trailingUpdated, flaggedExits, false, executedExits);
    }

    // A market order's requested price is only ever an estimate - the real
    // fill (and any slippage) is known solely by T212. EntryPrice/ExitPrice
    // are written optimistically at order-placement time (ExecutionService /
    // PositionExitService) and corrected here once T212 confirms the fill.
    private async Task ReconcileOrderFillsAsync(int accountId, ITrading212Client t212, CancellationToken ct)
    {
        List<Trade> pending;
        try
        {
            pending = (await tradeRepo.GetUnreconciledOrdersAsync(accountId)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load unreconciled orders for account {AccountId} — will retry next cycle", accountId);
            return;
        }

        logger.LogInformation("Fill reconciliation check for account {AccountId}: {Count} order(s) awaiting confirmation", accountId, pending.Count);
        if (pending.Count == 0) return;

        // This runs right after RunCycleAsync's own GetAccountSummaryAsync call,
        // and each GetOrderAsync below is itself a separate T212 call - same
        // rate-limit bucket PositionExitService already has to space its own
        // write call away from. One delay up front spaces this batch from the
        // summary call; one between each subsequent lookup spaces the batch
        // from itself, so N pending orders don't land back-to-back.
        foreach (var trade in pending)
        {
            if (ct.IsCancellationRequested) break;

            await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);

            var changed = false;

            if (trade.EntryOrderId is not null && trade.EntryFillConfirmedAt is null)
            {
                changed |= await TryReconcileOrderAsync(
                    t212, trade.EntryOrderId, trade.Symbol, accountId, "entry", ct,
                    onFilled: fillPrice => trade.EntryPrice = fillPrice,
                    markConfirmed: () => trade.EntryFillConfirmedAt = DateTime.UtcNow);

                if (trade.ExitOrderId is not null && trade.ExitFillConfirmedAt is null)
                    await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);
            }

            if (trade.ExitOrderId is not null && trade.ExitFillConfirmedAt is null)
                changed |= await TryReconcileOrderAsync(
                    t212, trade.ExitOrderId, trade.Symbol, accountId, "exit", ct,
                    onFilled: fillPrice =>
                    {
                        trade.ExitPrice = fillPrice;
                        trade.RealizedPnl = (fillPrice - trade.EntryPrice) * trade.Quantity;
                    },
                    markConfirmed: () => trade.ExitFillConfirmedAt = DateTime.UtcNow);

            if (changed)
                await tradeRepo.UpdateAsync(trade);
        }
    }

    // Returns true if the trade was mutated (fill confirmed, or the order
    // reached a terminal non-fill state and confirmation is being given up on
    // to stop polling it forever). Transient/lookup failures return false so
    // the same order is retried next cycle.
    private async Task<bool> TryReconcileOrderAsync(
        ITrading212Client t212, string orderId, string symbol, int accountId, string side, CancellationToken ct,
        Action<decimal> onFilled, Action markConfirmed)
    {
        OrderResponse order;
        try
        {
            order = await t212.GetOrderAsync(orderId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not fetch T212 order {OrderId} ({Side}, {Symbol}, account {AccountId}) — will retry next cycle", orderId, side, symbol, accountId);
            return false;
        }

        if (order.FillPrice.HasValue && order.FilledQuantity is > 0)
        {
            onFilled(order.FillPrice.Value);
            markConfirmed();
            logger.LogInformation(
                "{Side} fill confirmed for {Symbol} (account {AccountId}): order {OrderId} filled at £{FillPrice:F2}",
                side, symbol, accountId, orderId, order.FillPrice.Value);
            return true;
        }

        // NEW/WORKING orders haven't filled yet - keep polling. CANCELLED/REJECTED
        // never will, so stop polling and keep the estimated price rather than
        // retrying forever.
        var status = order.Status?.ToUpperInvariant() ?? "";
        if (status.Contains("CANCEL") || status.Contains("REJECT"))
        {
            markConfirmed();
            logger.LogWarning(
                "{Side} order {OrderId} for {Symbol} (account {AccountId}) ended as {Status} without a fill — keeping estimated price",
                side, orderId, symbol, accountId, order.Status);
            return true;
        }

        return false;
    }

    private static string ExitReasonDetail(ExitReason reason, Trade trade, decimal currentPrice) => reason switch
    {
        ExitReason.StopLossHit => $"Price ${currentPrice:F2} hit the stop loss (${trade.StopLossPrice:F2}).",
        ExitReason.TargetHit => $"Price ${currentPrice:F2} reached the target (${trade.TargetPrice:F2}).",
        ExitReason.TrailingStopHit => $"Price ${currentPrice:F2} hit the trailing stop (${trade.TrailingStopPrice:F2}).",
        ExitReason.TimeExit => $"Position held past the maximum hold period without hitting stop or target.",
        _ => $"Exit condition met at ${currentPrice:F2}.",
    };

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
