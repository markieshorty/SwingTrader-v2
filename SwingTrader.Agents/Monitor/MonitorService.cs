using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;
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
    IActivityLogRepository activityLog,
    IMarketRegimeService marketRegime,
    IFilingRepository filingRepo,
    IOptions<ExecutionConfig> executionConfig,
    IOptions<FilingDeltaConfig> filingDeltaConfig,
    ILogger<MonitorService> logger) : IMonitorService
{
    private readonly ExecutionConfig _execution = executionConfig.Value;


    public async Task<MonitorCycleResult> RunCycleAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITrading212Client t212,
        ITiingoClient? tiingo = null,
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

        // Step 0b — resolve any intent-first Pending placement whose broker
        // outcome was left unknown by Execution (crash/timeout mid-placement).
        // Promotes it to Open if the order actually reached T212, or Cancels it
        // if it never did - so it never becomes a duplicate or a stop-less ghost.
        await ReconcilePendingOrdersAsync(accountId, account.TradingMode, t212, ct);

        // Step 0c — position-drift check: compare the full set of local open
        // positions against the broker's ACTUAL holdings and alert on any
        // divergence (a local position the broker doesn't hold, a broker holding
        // with no local record, or a quantity mismatch). The fill/pending
        // reconciliation above is per-order; this is the holdings-level safety
        // net that catches state that has already drifted for any reason.
        // Detect-and-alert only: it never auto-closes, because acting on a
        // transient T212 blip would be worse than surfacing the drift.
        await ReconcilePositionsAsync(accountId, account.TradingMode, t212, ct);

        // Step 1 — circuit breaker check
        if (await circuitBreaker.ShouldTriggerAsync(accountId, summary, ct))
        {
            var openTrades = (await tradeRepo.GetOpenTradesAsync(accountId, account.TradingMode)).ToList();
            var flagged = openTrades.Select(t => new FlaggedExit(t.Symbol, ExitReason.CircuitBreaker, t.EntryPrice)).ToList();

            // Auto-pause new entries for the current mode so we stop opening
            // positions into a day that's already hit its loss limit. Stays
            // paused until the owner manually resumes (Settings > Trading) -
            // deliberately no auto-resume, so a continuing downturn doesn't
            // quietly re-arm trading. Guarded so we don't rewrite the row (or
            // clobber an existing manual pause's reason/timestamp) every
            // 5-minute cycle while the breaker stays tripped.
            var autoPaused = false;
            if (!account.IsExecutionPaused(account.TradingMode))
            {
                account.PauseExecution(account.TradingMode, ExecutionPauseReason.CircuitBreaker, DateTime.UtcNow);
                await accountRepo.UpdateAsync(account, ct);
                await activityLog.LogAsync(accountId, "SystemEvent", "Entries Auto-Paused", "Warning",
                    $"{account.TradingMode} entries auto-paused by the daily loss circuit breaker");
                autoPaused = true;
            }

            await SendAlertAsync(
                accountId,
                $"# \U0001F6A8 CIRCUIT BREAKER TRIGGERED\n\n" +
                $"The daily loss circuit breaker has fired for {openTrades.Count} open position(s): " +
                $"{string.Join(", ", openTrades.Select(t => t.Symbol))}.\n\n" +
                (autoPaused
                    ? $"**New {account.TradingMode} entries have been auto-paused** — resume them in Settings › Trading when you're ready.\n\n"
                    : "") +
                $"**No positions were closed automatically — review and close manually in Trading212.**",
                "\U0001F6A8 Acme Trading — CIRCUIT BREAKER TRIGGERED, manual review needed",
                NotificationCategory.CircuitBreaker);

            return new MonitorCycleResult(openTrades.Count, 0, flagged, true);
        }

        // Step 1b — detect the market regime, persist it (so the risk-profile
        // repository resolves the ACTIVE regime book for every consumer), and
        // apply that book's per-regime autopause. Entries pause while the
        // active book has AutopauseTrading on (seeded for Bear/Crisis) and
        // auto-resume the moment the regime moves to a book that permits them;
        // unlike the circuit breaker, only reason RegimeAutopause is ever
        // auto-resumed - a manual or circuit-breaker pause is never touched.
        // Skipped when no Tiingo client was supplied (regime needs SPY history)
        // or the check itself fails.
        await CheckRegimeAutopauseAsync(account, tiingo, finnhub, ct);

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

        // Distress exits (FD3): one query for every open symbol's active
        // distress flags (delisting/bankruptcy 8-K, going-concern filing).
        // A flagged position exits at market rather than waiting for the stop
        // - the whole point is getting out BEFORE the gap. Fail-open: a repo
        // error means no distress exits this cycle, never a stalled monitor.
        var distressBySymbol = new Dictionary<string, DistressFlag>(StringComparer.OrdinalIgnoreCase);
        if (trades.Count > 0)
        {
            try
            {
                var activeSince = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-filingDeltaConfig.Value.DistressWindowDays);
                var flags = await filingRepo.GetActiveDistressFlagsAsync(
                    trades.Select(t => t.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), activeSince, ct);
                foreach (var flag in flags)
                    distressBySymbol.TryAdd(flag.Symbol, flag); // newest-first from the repo
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Distress-flag lookup failed for account {AccountId} — distress exits skipped this cycle", accountId);
            }
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

                // Distress exit (FD3) beats the routine stop/target/trailing
                // checks: an active delisting/bankruptcy/going-concern flag
                // means the thesis is void - exit at market now, don't ride it
                // down to the stop. Retries next cycle if the order fails.
                if (distressBySymbol.TryGetValue(trade.Symbol, out var distress))
                {
                    var distressDetail = $"Distress exit: {distress.Reason} (filed {distress.FiledAt:yyyy-MM-dd}).";
                    var distressResult = await positionExit.ClosePositionAsync(
                        accountId, trade, t212, currentPrice, ExitReason.DistressExit, distressDetail, ct);

                    if (distressResult.Success)
                    {
                        executedExits.Add(new ExecutedExit(trade.Symbol, ExitReason.DistressExit, distressResult.ExitPrice!.Value, distressResult.RealizedPnl));
                    }
                    else
                    {
                        logger.LogWarning("{Symbol}: distress exit order failed — {Error}. Will retry next cycle.", trade.Symbol, distressResult.ErrorMessage);
                        flaggedExits.Add(new FlaggedExit(trade.Symbol, ExitReason.DistressExit, currentPrice));
                    }

                    checked_++;
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                // Rules frozen at buy time win over the live profile (see
                // Trade.MaxHoldDaysAtEntry) - profile changes only shape
                // positions opened after them. Nulls = legacy trades placed
                // before freezing existed; they follow the live profile.
                var result = await positionMonitor.CheckPositionAsync(
                    trade, currentPrice,
                    trade.MaxHoldDaysAtEntry ?? riskProfile.MaxHoldDays,
                    trade.TrailingActivationPctAtEntry ?? riskProfile.TrailingActivationPct,
                    trade.TrailingDistancePctAtEntry ?? riskProfile.TrailingDistancePct,
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
                $"Acme Trading — {flaggedExits.Count} position(s) failed to auto-close",
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
    // How long to wait for a Pending intent to appear in T212's order history
    // before concluding the order never reached the broker. A market order fills
    // within milliseconds and shows in history within seconds/minutes, so a
    // generous window makes a false "never placed" verdict effectively
    // impossible while still freeing reserved capital the same session.
    private const int PendingReconcileGraceMinutes = 30;

    private async Task CheckRegimeAutopauseAsync(Account account, ITiingoClient? tiingo, IFinnhubClient finnhub, CancellationToken ct)
    {
        if (tiingo is null) return;

        try
        {
            var regime = await marketRegime.GetCurrentRegimeAsync(tiingo, finnhub, ct);

            // Persist the detected regime so the risk-profile repository can
            // resolve the ACTIVE book for every consumer - this crosses the
            // API/Functions process boundary, where the in-memory regime cache
            // doesn't reach.
            if (account.CurrentMarketRegime != regime.Regime || account.RegimeUpdatedAt is null)
            {
                account.CurrentMarketRegime = regime.Regime;
                account.RegimeUpdatedAt = DateTime.UtcNow;
                await accountRepo.UpdateAsync(account, ct);
            }

            var mode = account.TradingMode;
            var regimePaused = account.IsExecutionPaused(mode)
                && account.ExecutionPauseReasonFor(mode) == ExecutionPauseReason.RegimeAutopause;

            // The active regime's book decides whether entries pause.
            var pauseWanted = (await riskProfileRepo.GetAsync(account.Id, regime.Regime, ct)).AutopauseTrading;

            if (pauseWanted && !account.IsExecutionPaused(mode))
            {
                account.PauseExecution(mode, ExecutionPauseReason.RegimeAutopause, DateTime.UtcNow);
                await accountRepo.UpdateAsync(account, ct);
                await activityLog.LogAsync(account.Id, "SystemEvent", "Entries Auto-Paused", "Warning",
                    $"{mode} entries auto-paused — {regime.Label}. Will auto-resume when the regime permits entries.");
                await SendAlertAsync(account.Id,
                    $"# ⏸️ Entries paused — {regime.Regime} regime\n\n" +
                    $"Market regime: **{regime.Label}**.\n\n" +
                    $"New {mode} entries are paused because this regime's risk book has auto-pause on; open positions are still " +
                    $"managed (stops, targets and exits keep working). Entries resume automatically when the regime moves to a " +
                    $"book that permits them. You can change this per regime on the Risk Management page.",
                    "Acme Trading — entries paused (market regime)",
                    NotificationCategory.CircuitBreaker);
                logger.LogWarning("Regime autopause engaged for account {AccountId} ({Mode}): {Label}", account.Id, mode, regime.Label);
            }
            else if (regimePaused && !pauseWanted)
            {
                account.ResumeExecution(mode);
                await accountRepo.UpdateAsync(account, ct);
                await activityLog.LogAsync(account.Id, "SystemEvent", "Entries Auto-Resumed", "Info",
                    $"{mode} entries auto-resumed — {regime.Label} permits entries.");
                await SendAlertAsync(account.Id,
                    $"# ✅ Entries resumed\n\n{mode} entries have auto-resumed — {regime.Label}.",
                    "Acme Trading — entries resumed",
                    NotificationCategory.CircuitBreaker);
                logger.LogInformation("Regime autopause released for account {AccountId} ({Mode}): {Label}", account.Id, mode, regime.Label);
            }
        }
        catch (Exception ex)
        {
            // A regime-fetch failure must never take down the monitor cycle -
            // the last-known regime and pause state carry to the next check.
            logger.LogWarning(ex, "Regime autopause check failed for account {AccountId} — will retry next cycle", account.Id);
        }
    }

    // A local position younger than this is exempt from the phantom/mismatch
    // checks: a just-placed order may not appear in T212's portfolio yet, and a
    // position mid-exit is briefly still open locally while its sell settles.
    private const int PositionDriftGraceMinutes = 20;

    private async Task ReconcilePositionsAsync(int accountId, TradingMode tradingMode, ITrading212Client t212, CancellationToken ct)
    {
        List<PortfolioPositionResponse> brokerPositions;
        try
        {
            // Space this from the cycle's other T212 calls, matching the fill
            // reconciliation's own pacing, to stay clear of the rate limit.
            await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);
            brokerPositions = await t212.GetPortfolioAsync() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch T212 portfolio for position reconciliation (account {AccountId}) — skipping this cycle", accountId);
            return;
        }

        List<Trade> openTrades, pendingTrades;
        try
        {
            openTrades = (await tradeRepo.GetOpenTradesAsync(accountId, tradingMode)).ToList();
            pendingTrades = (await tradeRepo.GetPendingTradesAsync(accountId, tradingMode)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load local positions for reconciliation (account {AccountId}) — skipping this cycle", accountId);
            return;
        }

        var settledCutoff = DateTime.UtcNow.AddMinutes(-PositionDriftGraceMinutes);
        var brokerHeld = brokerPositions.Where(p => Math.Abs(p.Quantity) > 0m).ToList();
        var settled = openTrades.Where(t => t.EntryFillConfirmedAt is not null && t.OpenedAt <= settledCutoff).ToList();

        // Wholesale-empty guard: a 200 response with zero holdings while
        // multiple settled positions are open locally is far more likely a
        // degraded T212 response (the demo API has returned bad-but-200 data
        // before - see the implausible-fill guard) or a deliberate manual
        // liquidation than N simultaneous unrecorded exits. Raise ONE
        // aggregated alert instead of flagging every position phantom every
        // cycle, and skip the per-position checks against data we don't trust.
        if (brokerHeld.Count == 0 && settled.Count > 0)
        {
            await LogPositionDriftAsync(accountId,
                $"Broker reports no holdings at all while {settled.Count} settled position(s) are open locally " +
                $"({string.Join(", ", settled.Select(t => t.Symbol))}) — degraded T212 response or full manual liquidation. Verify the account.", ct);
            return;
        }

        // 0) Non-US listing: a position whose captured BrokerTicker isn't a
        //    _US_EQ instrument was bought on a foreign listing (14 Jul 2026:
        //    a "HAL" buy landed on HAL1a_EQ, the Euronext Amsterdam listing
        //    of HAL Trust, while research scored Halliburton on US data).
        //    New buys can't create these any more (T212InstrumentResolver is
        //    US-only); this flags any that predate the fix for manual review.
        foreach (var trade in openTrades.Where(t =>
                     !string.IsNullOrEmpty(t.BrokerTicker)
                     && !Execution.T212InstrumentResolver.IsUsListing(t.BrokerTicker!)))
        {
            await LogPositionDriftAsync(accountId,
                $"{trade.Symbol}: held via non-US listing {trade.BrokerTicker} — research data and exit monitoring assume a US listing; review and close manually.", ct);
        }

        // 1) Phantom / quantity-mismatch: a confirmed, settled local open
        //    position that the broker either doesn't hold or holds a different
        //    quantity of. Fresh/unconfirmed positions are skipped (grace window).
        foreach (var trade in settled)
        {

            var match = brokerHeld.FirstOrDefault(p => TickerMatchesTrade(p.Ticker, trade));
            if (match is null)
            {
                await LogPositionDriftAsync(accountId,
                    $"{trade.Symbol}: open locally (qty {trade.Quantity:0.####}) but not held at the broker — possible unrecorded exit or manual close.", ct);
            }
            else if (Math.Abs(match.Quantity - trade.Quantity) > Math.Max(0.0001m, Math.Abs(trade.Quantity) * 0.01m))
            {
                await LogPositionDriftAsync(accountId,
                    $"{trade.Symbol}: quantity mismatch — local {trade.Quantity:0.####} vs broker {match.Quantity:0.####}.", ct);
            }
        }

        // 2) Untracked: a broker holding with no matching open OR pending local
        //    trade. Intent-first records every placement before it hits the
        //    broker, so an untracked holding is genuinely anomalous.
        foreach (var pos in brokerHeld)
        {
            var tracked = openTrades.Any(t => TickerMatchesTrade(pos.Ticker, t))
                || pendingTrades.Any(t => TickerMatchesTrade(pos.Ticker, t));
            if (!tracked)
            {
                await LogPositionDriftAsync(accountId,
                    $"{pos.Ticker}: held at the broker (qty {pos.Quantity:0.####}) with no matching open position on record.", ct);
            }
        }
    }

    private async Task LogPositionDriftAsync(int accountId, string message, CancellationToken ct)
    {
        logger.LogWarning("Position drift for account {AccountId}: {Message}", accountId, message);
        try
        {
            await activityLog.LogAsync(accountId, "SystemEvent", "Position Drift", "Warning", message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write position-drift activity log for account {AccountId}", accountId);
        }
    }

    private async Task ReconcilePendingOrdersAsync(int accountId, TradingMode tradingMode, ITrading212Client t212, CancellationToken ct)
    {
        List<Trade> pending;
        try
        {
            pending = (await tradeRepo.GetPendingTradesAsync(accountId, tradingMode)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load pending intents for account {AccountId} — will retry next cycle", accountId);
            return;
        }

        if (pending.Count == 0) return;
        logger.LogInformation("Pending-order reconciliation for account {AccountId}: {Count} intent(s) awaiting resolution", accountId, pending.Count);

        HistoricalOrdersResponse history;
        try
        {
            history = await t212.GetOrderHistoryAsync(limit: 50);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch T212 order history for pending reconciliation (account {AccountId}) — will retry next cycle", accountId);
            return;
        }

        foreach (var trade in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Match a filled history order to this intent by ticker (T212 tickers
            // look like "AAPL_US_EQ") and a fill at/after the intent time (minus a
            // small clock-skew allowance). At most one order per symbol per day is
            // placed, so the most recent match is unambiguous - and the time gate
            // stops a same-ticker order from a previous day matching by accident.
            // Fill.Quantity > 0 restricts the match to BUYS: T212 mirrors the
            // signed order quantity, so a sell reports negative - adopting a
            // same-ticker sell (manual sale, or an exit) as this intent's entry
            // would corrupt the trade with the sell's price and realised P&L.
            var match = history.Items
                .Where(i => i.Fill is not null
                    && i.Fill.Quantity > 0
                    && TickerMatchesSymbol(i.Order.Ticker, trade.Symbol)
                    && i.Fill.FilledAt >= trade.OpenedAt.AddMinutes(-5))
                .OrderByDescending(i => i.Fill!.FilledAt)
                .FirstOrDefault();

            if (match is not null)
            {
                // The order really did reach the broker - adopt its id and
                // promote to Open, applying the confirmed fill (same plausibility
                // guard as entry fill reconciliation) so the position is fully
                // reconciled in one step.
                trade.EntryOrderId = match.Order.Id.ToString();
                trade.Status = TradeStatus.Open;
                if (IsPlausibleFillPrice(match.Fill!.Price, trade.EntryPrice))
                {
                    trade.EntryPrice = match.Fill.Price;
                    trade.EntryValueGbp = match.Fill.WalletImpact?.NetValue;
                    trade.EntryFeesGbp = SumFeesGbp(match.Fill);
                }
                trade.EntryFillConfirmedAt = DateTime.UtcNow;
                await tradeRepo.UpdateAsync(trade);
                logger.LogWarning(
                    "Recovered pending order for {Symbol} (account {AccountId}): matched T212 order {OrderId} — promoted to Open",
                    trade.Symbol, accountId, match.Order.Id);
                continue;
            }

            // No matching order after the grace window → the placement never
            // reached the broker. Cancel the intent so its reserved capital frees
            // up. Within the window we leave it Pending and retry next cycle
            // (T212 history may simply not have caught up yet).
            if (DateTime.UtcNow - trade.OpenedAt > TimeSpan.FromMinutes(PendingReconcileGraceMinutes))
            {
                trade.Status = TradeStatus.Cancelled;
                await tradeRepo.UpdateAsync(trade);
                logger.LogWarning(
                    "Pending order for {Symbol} (account {AccountId}) had no matching T212 order after {Grace}m — marking Cancelled (never placed)",
                    trade.Symbol, accountId, PendingReconcileGraceMinutes);
                try
                {
                    await activityLog.LogAsync(accountId, "SystemEvent", "Order Not Placed", "Warning",
                        $"{trade.Symbol}: execution intent could not be confirmed at the broker and was cancelled.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to write activity log for cancelled pending order {Symbol} (account {AccountId})", trade.Symbol, accountId);
                }
            }
            else
            {
                logger.LogInformation(
                    "Pending order for {Symbol} (account {AccountId}) not yet in T212 history — within grace window, will retry",
                    trade.Symbol, accountId);
            }
        }
    }

    // Matches a broker holding's T212 ticker to a local trade. Prefers the
    // exact ticker persisted at placement (BrokerTicker) - T212 tickers carry
    // listing disambiguators the Symbol can't reproduce (e.g. "HAL1a_EQ" for
    // symbol "HAL"), and some instruments resolve by Name rather than ticker
    // prefix, so symbol-prefix matching alone produces false drift alerts.
    // Falls back to the symbol heuristic only for legacy trades placed before
    // BrokerTicker was captured.
    private static bool TickerMatchesTrade(string ticker, Trade trade) =>
        !string.IsNullOrEmpty(trade.BrokerTicker)
            ? ticker.Equals(trade.BrokerTicker, StringComparison.OrdinalIgnoreCase)
            : TickerMatchesSymbol(ticker, trade.Symbol);

    // T212 equity tickers carry a suffix, e.g. "AAPL_US_EQ" for symbol "AAPL",
    // and may insert a listing disambiguator between the base symbol and the
    // suffix, e.g. "HAL1a_EQ" for symbol "HAL". The base token (before the
    // first "_") therefore either equals the symbol or is the symbol followed
    // by a disambiguator made of digits/lowercase letters. Requiring the
    // remainder to be lowercase/digits avoids matching a genuinely different
    // symbol like "HALO" against "HAL".
    private static bool TickerMatchesSymbol(string ticker, string symbol)
    {
        if (ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase)) return true;

        var baseToken = ticker.Split('_', 2)[0];
        if (baseToken.Equals(symbol, StringComparison.OrdinalIgnoreCase)) return true;
        if (!baseToken.StartsWith(symbol, StringComparison.OrdinalIgnoreCase)) return false;

        var disambiguator = baseToken[symbol.Length..];
        return disambiguator.Length > 0 && disambiguator.All(c => char.IsDigit(c) || char.IsLower(c));
    }

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
                        // T212 (notably the demo endpoint) has been seen returning
                        // an implausible fill price - e.g. Halliburton "filled" at
                        // 165 when the placement quote was ~34.86 - which overwrote
                        // a good EntryPrice and made the position read as a ~-79%
                        // loss. A real market-order fill is within a small slippage
                        // of the placement price, never a multiple of it, so reject
                        // a wildly-off fill as bad data and keep the placement price
                        // (which the stop/target were anchored to). markConfirmed()
                        // still runs, so the same bad fill isn't re-pulled each cycle.
                        if (IsPlausibleFillPrice(fill.Price, trade.EntryPrice))
                        {
                            trade.EntryPrice = fill.Price;
                            trade.EntryValueGbp = fill.WalletImpact?.NetValue;
                            trade.EntryFeesGbp = SumFeesGbp(fill);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Rejecting implausible entry fill for {Symbol} (account {AccountId}, order {OrderId}): " +
                                "fill price {FillPrice} is not within 0.5x-2x of the placement price {PlacementPrice} — keeping placement price",
                                trade.Symbol, accountId, trade.EntryOrderId, fill.Price, trade.EntryPrice);
                        }
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

    // A genuine market-order fill lands within a small slippage of the price we
    // placed at. Anything outside a wide sanity band (½× to 2×) is bad data - a
    // T212 demo glitch or a mismatched fill - not slippage, and must not be
    // allowed to overwrite the placement price.
    private static bool IsPlausibleFillPrice(decimal fillPrice, decimal placementPrice) =>
        fillPrice > 0 && placementPrice > 0 &&
        fillPrice >= placementPrice * 0.5m && fillPrice <= placementPrice * 2m;

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
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update portfolio snapshot after monitor cycle for account {AccountId}", accountId);
        }
    }
}
