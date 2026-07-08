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
    IAccountRepository accountRepo,
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

        var account = await accountRepo.GetAsync(accountId, ct);
        if (account is null)
        {
            logger.LogWarning("No account record found for account {AccountId} — skipping monitor cycle", accountId);
            return new MonitorCycleResult(0, 0, [], false);
        }

        // Step 0 — reconcile any order placed last cycle (or earlier) whose
        // real fill price T212 hadn't confirmed yet. Runs before the circuit
        // breaker check so RealizedPnl/EntryPrice/ExitPrice are corrected as
        // early as possible once T212 confirms.
        await ReconcileOrderFillsAsync(accountId, account.TradingMode, t212, ct);

        // Step 1 — circuit breaker check
        if (await circuitBreaker.ShouldTriggerAsync(accountId, summary, ct))
        {
            var openTrades = (await tradeRepo.GetOpenTradesAsync(accountId, account.TradingMode)).ToList();
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
        var trades = (await tradeRepo.GetOpenTradesAsync(accountId, account.TradingMode)).ToList();

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
    private async Task ReconcileOrderFillsAsync(int accountId, TradingMode tradingMode, ITrading212Client t212, CancellationToken ct)
    {
        List<Trade> pending;
        try
        {
            pending = (await tradeRepo.GetUnreconciledOrdersAsync(accountId, tradingMode)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load unreconciled orders for account {AccountId} — will retry next cycle", accountId);
            return;
        }

        logger.LogInformation("Fill reconciliation check for account {AccountId}: {Count} order(s) awaiting confirmation", accountId, pending.Count);
        if (pending.Count == 0) return;

        // GetOrderAsync (single-order lookup) only returns currently-working
        // orders - a market order fills within milliseconds and 404s on that
        // endpoint moments later (confirmed live: every pending order lookup
        // 404'd, including ones placed under 30 minutes earlier). One history
        // fetch per cycle replaces what used to be one GetOrderAsync call per
        // pending order - fewer T212 calls, and it's the only endpoint that
        // actually reports a filled order's real price. One delay spaces this
        // from RunCycleAsync's own GetAccountSummaryAsync call this cycle.
        await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);

        Dictionary<long, HistoricalOrderItem> byId;
        try
        {
            var history = await t212.GetOrderHistoryAsync(limit: 50);
            byId = history.Items.ToDictionary(i => i.Order.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch T212 order history for account {AccountId} — will retry next cycle", accountId);
            return;
        }

        foreach (var trade in pending)
        {
            if (ct.IsCancellationRequested) break;

            var changed = false;

            if (trade.EntryOrderId is not null && trade.EntryFillConfirmedAt is null)
                changed |= TryReconcileOrder(
                    byId, trade.EntryOrderId, trade.Symbol, accountId, "entry",
                    onFilled: fill =>
                    {
                        trade.EntryPrice = fill.Price;
                        trade.EntryValueGbp = fill.WalletImpact?.NetValue;
                        trade.EntryFeesGbp = SumFeesGbp(fill);
                    },
                    markConfirmed: () => trade.EntryFillConfirmedAt = DateTime.UtcNow);

            if (trade.ExitOrderId is not null && trade.ExitFillConfirmedAt is null)
                changed |= TryReconcileOrder(
                    byId, trade.ExitOrderId, trade.Symbol, accountId, "exit",
                    onFilled: fill =>
                    {
                        trade.ExitPrice = fill.Price;
                        trade.ExitValueGbp = fill.WalletImpact?.NetValue;
                        trade.ExitFeesGbp = SumFeesGbp(fill);
                        // T212's own realisedProfitLoss is the authoritative P&L
                        // once known - it accounts for FX conversion and fees
                        // that a naive (fillPrice - EntryPrice) * Quantity
                        // calculation in the instrument's own currency misses
                        // entirely. Falls back to the estimate only if T212
                        // didn't return it for some reason.
                        trade.RealizedPnl = fill.WalletImpact?.RealisedProfitLoss
                            ?? (fill.Price - trade.EntryPrice) * trade.Quantity;
                    },
                    markConfirmed: () => trade.ExitFillConfirmedAt = DateTime.UtcNow);

            if (changed)
                await tradeRepo.UpdateAsync(trade);
        }
    }

    // Positive £ total of whatever fees (e.g. CURRENCY_CONVERSION_FEE) T212
    // charged on this fill - quantity is reported negative (a deduction).
    private static decimal? SumFeesGbp(HistoricalFillDetail fill) =>
        fill.WalletImpact?.Taxes is { Count: > 0 } taxes ? -taxes.Sum(t => t.Quantity) : null;

    // Returns true if the trade was mutated (fill confirmed, or the order
    // reached a terminal non-fill state and confirmation is being given up on
    // to stop polling it forever). Order not yet present in the most recent
    // 50 history items returns false so it's retried next cycle - it'll
    // appear once T212's history catches up (normally within seconds/minutes).
    private bool TryReconcileOrder(
        Dictionary<long, HistoricalOrderItem> byId, string orderId, string symbol, int accountId, string side,
        Action<HistoricalFillDetail> onFilled, Action markConfirmed)
    {
        if (!long.TryParse(orderId, out var id) || !byId.TryGetValue(id, out var item))
            return false;

        var order = item.Order;
        // A sell order's quantity/filledQuantity is negative (T212 mirrors
        // whatever signed quantity was sent in the original order request -
        // PositionExitService places sells as -trade.Quantity), so this must
        // check for "filled at all" rather than "filled positive" - the >0
        // check here previously matched every buy fine but silently never
        // matched a single sell, leaving every exit stuck "unconfirmed"
        // forever despite the order genuinely being FILLED in T212's history.
        if (item.Fill is not null && order.FilledQuantity is not null and not 0)
        {
            onFilled(item.Fill);
            markConfirmed();
            logger.LogInformation(
                "{Side} fill confirmed for {Symbol} (account {AccountId}): order {OrderId} filled at ${FillPrice:F2} (£{NetValue:F2})",
                side, symbol, accountId, orderId, item.Fill.Price, item.Fill.WalletImpact?.NetValue);
            return true;
        }

        // NEW/CONFIRMED/etc. orders haven't filled yet - keep polling.
        // CANCELLED/REJECTED never will, so stop polling and keep the
        // estimated price rather than retrying forever.
        var status = order.Status.ToUpperInvariant();
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
            var account = await accountRepo.GetAsync(accountId);
            if (account is null)
            {
                logger.LogWarning("No account record found for account {AccountId} — skipping portfolio snapshot update", accountId);
                return;
            }

            // TotalValue/Investments.CurrentValue are already in the
            // account's base currency (GBP) - T212 computes these itself.
            var totalValue = summary.TotalValue;
            var openValue = summary.Investments.CurrentValue;

            await portfolioRepo.AddAsync(new PortfolioSnapshot
            {
                AccountId = accountId,
                TradingMode = account.TradingMode,
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
