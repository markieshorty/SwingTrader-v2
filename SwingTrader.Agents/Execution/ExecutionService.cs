using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Services;
using SwingTrader.Core.Trading;

namespace SwingTrader.Agents.Execution;

public class ExecutionService(
    IAccountAllocationRepository allocationRepo,
    ISignalRepository signalRepo,
    ITradeRepository tradeRepo,
    IPortfolioRepository portfolioRepo,
    IApprovalRepository approvalRepo,
    IAccountRepository accountRepo,
    IPositionSizingService sizingService,
    IAccountRiskProfileRepository riskProfileRepo,
    ISetupTacticsRepository setupTacticsRepo,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    IMemoryCache cache,
    IForexService forex,
    IEntryConfirmationService entryConfirmation,
    IActivityLogRepository activityLog,
    IMarketRegimeService marketRegimeService,
    IFinnhubRateLimiter rateLimiter,
    IOptions<ExecutionConfig> executionConfig,
    ILogger<ExecutionService> logger) : IExecutionService
{
    private readonly ExecutionConfig _execution = executionConfig.Value;

    public async Task<ExecutionResult> RunAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITiingoClient tiingo,
        ITrading212Client t212,
        DateOnly date,
        CancellationToken ct = default)
    {
        // Step 1 — check approval gate (ApprovalRequired is a per-account
        // setting, Settings page - not a global environment flag).
        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        // Pause gate — the Settings > Trading pause switch, held per mode. When
        // paused, place no new buys (Monitor still manages open positions, so
        // stops/targets keep working). Checked before the approval gate so a
        // paused account never even looks for an approval row.
        if (account.IsExecutionPaused(account.TradingMode))
        {
            logger.LogInformation("Execution skipped for account {AccountId} on {Date} — entries paused for {Mode}",
                accountId, date, account.TradingMode);
            return new ExecutionResult(0, 0, 0, "Entries paused", []);
        }

        HashSet<string>? approvedSymbols = null;
        if (account.ApprovalRequired)
        {
            var approval = await approvalRepo.GetByDateAsync(accountId, account.TradingMode, date);
            if (approval is null || !approval.IsApproved)
            {
                logger.LogWarning("Execution skipped for account {AccountId} on {Date} — no approval found", accountId, date);
                return new ExecutionResult(0, 0, 0, "No approval for today", []);
            }
            if (!string.IsNullOrWhiteSpace(approval.ApprovedSymbols))
                approvedSymbols = approval.ApprovedSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToUpperInvariant())
                    .ToHashSet();
        }

        var riskProfile = await riskProfileRepo.GetAsync(accountId, ct);

        // Step 2 — load eligible signals
        // Excludes symbols closed earlier today (by ClosedAt, not signal.WasExecuted
        // alone) - a same-day re-enqueue after an exit frees capital (see
        // PositionExitService.ReenqueueExecutionIfDoneForTodayAsync) would otherwise
        // immediately re-buy the exact symbol just sold if its signal is still
        // sitting there approved. Resets naturally the next day - a fresh Research
        // run is free to re-recommend the same symbol tomorrow.
        var closedTodaySymbols = (await tradeRepo.GetClosedOnDateAsync(accountId, account.TradingMode, date))
            .Select(t => t.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An unresolved Pending intent (broker outcome still unknown, Monitor
        // hasn't reconciled it yet) blocks new entries for that symbol: if the
        // original order actually filled, placing again would duplicate the
        // position. Same-day this is already covered by the signal's
        // WasExecuted claim - this guards the stale case (e.g. Monitor down
        // long enough for an intent to survive into the next day's signals).
        var pendingSymbols = (await tradeRepo.GetPendingTradesAsync(accountId, account.TradingMode))
            .Select(t => t.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allSignals = (await signalRepo.GetByDateAsync(accountId, date))
            .Where(s => s.Recommendation == Recommendation.Buy && !s.WasExecuted
                && s.BrokerRejectedAt == null
                && !closedTodaySymbols.Contains(s.Symbol) && !pendingSymbols.Contains(s.Symbol))
            // Buy PRIORITY = combined score 0-20 (gate + forward), 6 Aug
            // 2026: with scarce slots, ordering by gate alone let a
            // technically-loud signal with a weak forward outlook out-rank a
            // solid signal Claude actually liked. Gate still decides
            // ELIGIBILITY, forward still decides SIZE - this only decides
            // who goes first when not everything fits. A missing/degraded
            // forward counts as neutral 5, never as 0 (an outage must not
            // reshuffle the queue).
            .OrderByDescending(s => (s.ConvictionScore ?? 0m)
                + (s.ForwardScoreDegraded ? 5m : s.ForwardScore ?? 5m))
            .ToList();

        if (approvedSymbols is not null)
            allSignals = allSignals.Where(s => approvedSymbols.Contains(s.Symbol)).ToList();

        var signals = allSignals.Take(_execution.MaxOrdersPerDay).ToList();

        if (signals.Count == 0)
        {
            logger.LogInformation("No eligible signals to execute for account {AccountId} on {Date}", accountId, date);
            return new ExecutionResult(0, 0, allSignals.Count, "No eligible signals", []);
        }

        // Step 3 — verify account state.
        // Monitor runs every 5 minutes and calls the same T212 endpoint; execution
        // fires immediately after, so a brief initial pause lets Monitor's call clear
        // before we hit the same rate limit bucket.
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        T212AccountSummary accountSummary;
        {
            Exception? lastEx = null;
            accountSummary = null!;
            int[] retryDelaysSeconds = [15, 30, 60];
            for (int attempt = 0; attempt <= retryDelaysSeconds.Length; attempt++)
            {
                try
                {
                    accountSummary = await t212.GetAccountSummaryAsync();
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < retryDelaysSeconds.Length)
                    {
                        logger.LogWarning(ex,
                            "T212 account summary failed for account {AccountId} (attempt {Attempt}/{Max}) — retrying in {Delay}s",
                            accountId, attempt + 1, retryDelaysSeconds.Length + 1, retryDelaysSeconds[attempt]);
                        await Task.Delay(TimeSpan.FromSeconds(retryDelaysSeconds[attempt]), ct);
                    }
                }
            }
            if (accountSummary is null)
            {
                logger.LogError(lastEx, "Failed to retrieve T212 account summary for account {AccountId} after all retries — aborting execution", accountId);
                return new ExecutionResult(0, 0, signals.Count, "Account summary unavailable", []);
            }
        }

        // Defensive: a real account is never actually worth £0. Sizing
        // trades against a bad £0 budget would be worse than just skipping
        // this run if T212 ever returns a degraded/incomplete 200 response.
        if (accountSummary.TotalValue <= 0)
        {
            logger.LogError(
                "T212 account summary returned a non-positive total ({Total:F2}) for account {AccountId} — aborting execution",
                accountSummary.TotalValue, accountId);
            return new ExecutionResult(0, 0, signals.Count, "Account summary looked invalid (zero total)", []);
        }

        var availableCash = accountSummary.Cash.AvailableToTrade;
        try
        {
            // The cash endpoint exposes fields the summary hides (notably
            // 'blocked') - logged while diagnosing 440's phantom
            // insufficient-funds rejections (30 Jul 2026).
            var cashDetail = await t212.GetAccountCashAsync();
            logger.LogInformation(
                "T212 cash detail for account {AccountId}: free={Free:F2} total={Total:F2} blocked={Blocked:F2} invested={Invested:F2} pieCash={PieCash:F2}",
                accountId, cashDetail.Free, cashDetail.Total, cashDetail.Blocked, cashDetail.Invested, cashDetail.PieCash);
            if (cashDetail.Free > 0 && cashDetail.Free < availableCash)
            {
                logger.LogWarning(
                    "T212 'free' ({Free:F2}) is below the summary's availableToTrade ({Available:F2}) for account {AccountId} — sizing from the lower figure",
                    cashDetail.Free, availableCash, accountId);
                availableCash = cashDetail.Free;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch T212 cash detail for account {AccountId} — using the summary figure", accountId);
        }
        var allOpenTrades = (await tradeRepo.GetOpenTradesAsync(accountId, account.TradingMode)).ToList();
        // Swing-sleeve scope (docs/sleeves-plan P1): slots, sizing and the
        // deployable check see only swing positions; other sleeves hold
        // their own capital.
        var openTrades = allOpenTrades.Where(t => t.Sleeve == SleeveType.Swing).ToList();

        // Cash/portfolio figures are in the account's BASE currency - which is
        // whatever the T212 account was opened in, not necessarily GBP. The
        // signal price is USD; convert it into the account currency before
        // sizing so budget and price share a currency. Assuming GBP for a
        // USD-denominated account inflated every quantity by ~34% and made
        // T212 refuse each order as insufficient funds (account 440, 30 Jul
        // 2026: summary "free 2499" was DOLLARS - a £2,135 ≈ $2,854 order
        // could never fit).
        var usdToBase = 1m;
        try
        {
            var info = await t212.GetAccountInfoAsync();
            if (string.Equals(info.CurrencyCode, "GBP", StringComparison.OrdinalIgnoreCase))
                usdToBase = await forex.GetGbpUsdRateAsync(ct);
            else if (!string.Equals(info.CurrencyCode, "USD", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning("Account {AccountId} T212 currency is {Currency} — no FX conversion available, sizing as if USD",
                    accountId, info.CurrencyCode);
            logger.LogInformation("Account {AccountId} T212 base currency: {Currency} (usdToBase={Rate:F4})",
                accountId, info.CurrencyCode, usdToBase);
        }
        catch (Exception ex)
        {
            // Info endpoint down: fall back to the long-standing GBP assumption.
            usdToBase = await forex.GetGbpUsdRateAsync(ct);
            logger.LogWarning(ex, "Could not read T212 account currency for {AccountId} — assuming GBP", accountId);
        }
        var gbpUsd = usdToBase;

        // TotalValue/Investments.CurrentValue are already in the account's
        // base currency (GBP), computed by T212 itself.
        // Sleeve scoping (docs/sleeves-plan P1): the swing strategy sizes
        // against its SLICE of equity, and other sleeve holdings do not
        // count toward its deployable usage. Default allocation (Swing 100%)
        // makes every number below identical to the pre-sleeves behaviour.
        var allocation = await allocationRepo.GetAsync(accountId, ct);
        var nonSwingValue = allOpenTrades
            .Where(t => t.Sleeve != SleeveType.Swing)
            .Sum(t => t.EntryValueGbp ?? t.Quantity * t.EntryPrice);
        var openPositionsValue = Math.Max(0m, accountSummary.Investments.CurrentValue - nonSwingValue);
        var totalPortfolioValue = accountSummary.TotalValue * allocation.SwingPct;
        logger.LogInformation(
            "Execution starting for account {AccountId}: {Date} | Cash={Cash:F2} | ReservedForOrders={Reserved:F2} | InPies={Pies:F2} | OpenPositionsValue={Positions:F2} | TotalPortfolio={Portfolio:F2} | Signals={Count}",
            accountId, date, availableCash, accountSummary.Cash.ReservedForOrders, accountSummary.Cash.InPies,
            openPositionsValue, totalPortfolioValue, signals.Count);

        // Pre-fetch instruments once to avoid 429s from per-symbol calls
        var instrumentsCacheKey = $"t212_instruments_all_{accountId}";
        try
        {
            var instruments = await t212.GetInstrumentsAsync();
            cache.Set(instrumentsCacheKey, instruments, TimeSpan.FromHours(24));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not pre-fetch T212 instruments for account {AccountId} — will fall back to per-symbol lookup", accountId);
        }

        // Step 4 — execute signals
        int placed = 0, failed = 0, skipped = 0;
        var placedSymbols = new List<string>();
        // Intraday-confirmation skips with reasons, surfaced in the execution
        // email so a silent gate can never quietly eat the day's entries.
        var entrySkips = new List<string>();
        // Per-order detail for the execution email - the counts alone said
        // nothing about WHAT was bought or why anything failed/skipped.
        var boughtRows = new List<string>();   // markdown table rows
        var failedLines = new List<string>();
        var skippedLines = new List<string>();
        // GBP deployed by THIS run's placements - added to the broker's
        // openPositionsValue for the cumulative active-capital check, since the
        // broker total won't reflect just-placed orders yet.
        var deployedThisRun = 0m;

        foreach (var signal in signals)
        {
            if (ct.IsCancellationRequested)
            {
                logger.LogWarning("Shutdown requested mid-execution for account {AccountId} — stopping before placing further orders", accountId);
                break;
            }

            if (openTrades.Any(t => t.Symbol == signal.Symbol))
            {
                logger.LogInformation("Skipping {Symbol}: already have an open position (account {AccountId})", signal.Symbol, accountId);
                skippedLines.Add($"**{signal.Symbol}** — already holding a position");
                skipped++;
                continue;
            }

            // Size from a LIVE quote, not the signal's 7:30 ET research price:
            // by the open the stock can have gapped enough that a quantity
            // computed off the stale price costs more than the estimate and
            // T212 rejects the market order with insufficient funds (seen
            // live 30 Jul 2026). The same quote re-anchors stop/target below.
            decimal? livePrice = null;
            try
            {
                await rateLimiter.WaitAsync(ct);
                var quote = await finnhub.GetQuoteAsync(signal.Symbol);
                if (quote.CurrentPrice is > 0) livePrice = quote.CurrentPrice;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch live quote for {Symbol} (account {AccountId}) — sizing from the research price", signal.Symbol, accountId);
            }

            var sizing = await sizingService.CalculateAsync(
                signal, openTrades.Count, availableCash, totalPortfolioValue, riskProfile,
                priceOverride: (livePrice ?? signal.CurrentPrice) * gbpUsd,
                openPositionsValue: openPositionsValue + deployedThisRun,
                usdToBaseRate: gbpUsd);

            if (!sizing.CanTrade)
            {
                logger.LogInformation("Skipping {Symbol}: {Reason} (account {AccountId})", signal.Symbol, sizing.RejectionReason, accountId);
                skippedLines.Add($"**{signal.Symbol}** — {sizing.RejectionReason}");
                skipped++;
                continue;
            }

            string? ticker;
            try
            {
                ticker = await ResolveT212TickerAsync(accountId, instrumentsCacheKey, t212, signal.Symbol);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not resolve T212 ticker for {Symbol} (account {AccountId}) — skipping", signal.Symbol, accountId);
                failedLines.Add($"**{signal.Symbol}** — could not resolve the T212 instrument ({ex.Message})");
                failed++;
                continue;
            }

            if (ticker is null)
            {
                logger.LogWarning("No T212 instrument found for {Symbol} (account {AccountId}) — skipping", signal.Symbol, accountId);
                skippedLines.Add($"**{signal.Symbol}** — no matching T212 instrument");
                skipped++;
                continue;
            }

            // signal.CalculatedStopLoss/CalculatedTarget are absolute price
            // levels computed from whatever price was live when Report ran
            // (~6:30 ET) - by the time an order actually places (immediately
            // at Execution's 9:20 ET window, or hours later via a same-day
            // approval), the stock can have moved enough that those fixed
            // levels no longer sit at their intended distance from the real
            // entry price. The percentage table itself doesn't depend on any
            // particular price snapshot, so re-deriving it from a quote taken
            // right before order placement keeps the stop/target correctly
            // anchored regardless of how stale the signal's own price is.
            // Falls back to the signal's precomputed levels if this fails -
            // not worth blocking the trade over one quote call.
            // Entry/exit tactics come from the SETUP that triggered the signal
            // (docs/setup-tactics-plan) - stop, target, guide-hold and trailing.
            // The regime risk book stays the exposure envelope. Falls back to
            // the risk book if this setup has no tactics row (e.g. Unknown).
            var tactics = await setupTacticsRepo.GetAsync(accountId, signal.SetupType, ct);
            var stopPct = tactics?.StopLossPct ?? riskProfile.StopLossPct;
            var targetPct = tactics?.TargetPct ?? riskProfile.TargetPct;
            var guideHoldDays = tactics?.GuideHoldDays ?? riskProfile.MaxHoldDays;
            var trailingActivation = tactics?.TrailingActivationPct ?? riskProfile.TrailingActivationPct;
            var trailingDistance = tactics?.TrailingDistancePct ?? riskProfile.TrailingDistancePct;

            // Dynamic target (1 Aug 2026): derive the effective target from
            // the stock's own behaviour when the book asks for it; Flat mode
            // returns targetPct untouched. Runs BEFORE level derivation so
            // both the precomputed fallback path and the fresh-quote path use
            // the same effective percentage. (ATR sizing STYLE still wins
            // below - its ATR-anchored levels override these.)
            targetPct = Core.Trading.DynamicTarget.ResolvePct(
                riskProfile.TargetMode, targetPct, signal.Atr14,
                livePrice ?? signal.CurrentPrice, signal.NearestResistance,
                riskProfile.AtrTargetMultiple, riskProfile.TargetBandFloorPct, riskProfile.TargetBandCeilingPct);

            var stopLossPrice = signal.CalculatedStopLoss ?? signal.CurrentPrice * (1 - stopPct);
            var targetPrice = signal.CalculatedTarget ?? signal.CurrentPrice * (1 + targetPct);
            if (livePrice is { } lp)
            {
                var (freshStop, freshTarget) = EntryLevelCalculator.Calculate(lp, stopPct, targetPct);
                stopLossPrice = freshStop;
                targetPrice = freshTarget;
            }

            // ATR-anchored levels (sizing-style toggle): a stop k ATRs below
            // entry means "k normal days of adverse movement" for EVERY stock,
            // where a flat percentage is noise on a volatile name and glacial
            // on a calm one. Overrides the percentage levels (per-setup
            // tactics included) while the style is on; missing/degenerate ATR
            // keeps the percentage levels just derived above.
            if (riskProfile.SizingStyle == SizingStyle.AtrRiskParity && signal.Atr14 is { } atrEntry && atrEntry > 0)
            {
                var anchor = livePrice ?? signal.CurrentPrice;
                var atrStop = anchor - riskProfile.AtrStopMultiple * atrEntry;
                if (atrStop > 0)
                {
                    stopLossPrice = atrStop;
                    targetPrice = anchor + riskProfile.AtrTargetMultiple * atrEntry;
                }
            }

            // ── Intraday entry confirmation (flagged, default off) ───────────
            // Runs BEFORE the intent-first persist: a rejected entry leaves no
            // Pending row and does NOT claim the signal (WasExecuted stays
            // false), so a later same-day re-run can still buy if conditions
            // normalise - gaps fade. Unavailable data fails OPEN: buy exactly
            // as if the gate didn't exist.
            if (_execution.IntradayConfirmationEnabled)
            {
                var confirmation = await entryConfirmation.ConfirmAsync(
                    tiingo, signal.Symbol, signal.CurrentPrice, stopLossPrice, ct);
                if (confirmation.Verdict == EntryConfirmationVerdict.Rejected)
                {
                    logger.LogInformation("Entry skipped for {Symbol} (account {AccountId}) — {Reason}",
                        signal.Symbol, accountId, confirmation.Reason);
                    try
                    {
                        await activityLog.LogAsync(accountId, "TradeEvent", "Entry skipped", "Skipped",
                            $"{signal.Symbol}: {confirmation.Reason}", ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not write entry-skip activity log for {Symbol}", signal.Symbol);
                    }
                    entrySkips.Add($"{signal.Symbol} — {confirmation.Reason}");
                    skipped++;
                    continue;
                }
                if (confirmation.Verdict == EntryConfirmationVerdict.Unavailable)
                    logger.LogInformation("Entry confirmation unavailable for {Symbol} (account {AccountId}) — proceeding (fail open)",
                        signal.Symbol, accountId);
            }

            // ── Intent-first placement ────────────────────────────────────────
            // Service Bus delivers execution messages at-least-once, so this
            // handler can be redelivered after a crash/timeout. Persist the trade
            // as Pending AND claim the signal (WasExecuted) *before* the broker
            // call: a redelivery then sees the signal already executed (skipped)
            // and the position tracked as Pending, instead of re-placing a
            // duplicate order or leaving an untracked, stop-less position. The
            // Pending row carries no EntryOrderId yet - Monitor's pending
            // reconciliation promotes it to Open (or Cancels it) against T212's
            // order history.
            var trade = new Trade
            {
                AccountId = accountId,
                TradingMode = account.TradingMode,
                Symbol = signal.Symbol,
                BrokerTicker = ticker,
                CompanyName = signal.CompanyName,
                Direction = TradeDirection.Long,
                EntryPrice = signal.CurrentPrice,
                Quantity = sizing.Quantity,
                StopLossPrice = stopLossPrice,
                TargetPrice = targetPrice,
                Status = TradeStatus.Pending,
                OpenedAt = DateTime.UtcNow,
                SignalId = signal.Id,
                // Funnel F2 scorecard fields: what the forward score said at
                // entry and how much it tilted the size (1 = untilted).
                ForwardScoreAtEntry = signal.ForwardScore,
                SizeMultiplier = sizing.AppliedMultiplier,
                // Rules frozen at entry (thesis-as-contract) - config changes
                // only affect positions opened after them. See Trade.cs. Stop/
                // target/hold/trailing come from the setup's tactics; the
                // probation floor + momentum bar stay on the regime book.
                MaxHoldDaysAtEntry = guideHoldDays,
                MinHoldDaysAtEntry = riskProfile.MinHoldDays,
                MomentumHealthThresholdAtEntry = riskProfile.MomentumHealthThreshold,
                TrailingActivationPctAtEntry = trailingActivation,
                TrailingDistancePctAtEntry = trailingDistance,
            };
            try
            {
                await tradeRepo.AddAsync(trade);
                signal.WasExecuted = true;
                await signalRepo.UpdateAsync(signal);
            }
            catch (Exception ex)
            {
                // No broker call has been made yet, so skipping is safe - nothing
                // to reconcile. Retry naturally happens if the message redelivers.
                logger.LogError(ex, "Failed to record execution intent for {Symbol} (account {AccountId}) — skipping before any order placed", signal.Symbol, accountId);
                failedLines.Add($"**{signal.Symbol}** — could not record the trade intent; no order was placed");
                failed++;
                continue;
            }

            try
            {
                // T212 sometimes refuses orders that its own cash figures say
                // should fit (440, 30 Jul 2026: £2,136 refused with £2,499
                // free, nothing blocked/reserved/in pies, GBP account). Until
                // the broker-side rule is understood, self-discover the
                // acceptable size: retry the same order at 75% then 50%
                // quantity before giving up. A smaller position beats no
                // position during the diagnose phase.
                OrderResponse order = null!;
                var placedQuantity = sizing.Quantity;
                var attemptScales = new[] { 1.0m, 0.75m, 0.5m };
                for (var i = 0; i < attemptScales.Length; i++)
                {
                    placedQuantity = Math.Floor(sizing.Quantity * attemptScales[i] * 1000m) / 1000m;
                    try
                    {
                        order = await t212.PlaceMarketOrderAsync(new MarketOrderRequest(ticker, placedQuantity));
                        if (i > 0)
                        {
                            trade.Quantity = placedQuantity;
                            await activityLog.LogAsync(accountId, "TradeEvent", "Order Downsized", "Warning",
                                $"{signal.Symbol} ({ticker}): T212 refused the full size — placed at {attemptScales[i]:P0} ({placedQuantity:0.###} shares, ~£{sizing.EstimatedCost * attemptScales[i]:F2}).", ct);
                            logger.LogWarning("Placed {Symbol} at {Scale:P0} after insufficient-funds refusals (account {AccountId})",
                                signal.Symbol, attemptScales[i], accountId);
                        }
                        break;
                    }
                    catch (Refit.ApiException api) when (i < attemptScales.Length - 1
                        && (int)api.StatusCode is >= 400 and < 500
                        && api.Content?.Contains("insufficient-free-for-stocks-buy") == true)
                    {
                        logger.LogWarning("T212 refused {Symbol} at {Scale:P0} size (account {AccountId}) — retrying smaller",
                            signal.Symbol, attemptScales[i], accountId);
                    }
                }

                trade.EntryOrderId = order.Id.ToString();
                trade.Status = TradeStatus.Open;
                await PopulateMarketContextAsync(trade, finnhub, tiingo, ct);
                await tradeRepo.UpdateAsync(trade);
                openTrades.Add(trade);

                logger.LogInformation(
                    "Order placed for account {AccountId}: {Symbol} ({Ticker}) qty={Qty} estimatedCost={Cost:F2} orderId={OrderId}",
                    accountId, signal.Symbol, ticker, sizing.Quantity, sizing.EstimatedCost, order.Id);

                availableCash -= sizing.EstimatedCost;
                deployedThisRun += sizing.EstimatedCost;
                placedSymbols.Add(signal.Symbol);
                var placedCostGbp = sizing.Quantity > 0
                    ? sizing.EstimatedCost * (placedQuantity / sizing.Quantity)
                    : sizing.EstimatedCost;
                var downsizedNote = placedQuantity < sizing.Quantity ? " ⚠️ downsized" : string.Empty;
                boughtRows.Add(
                    $"| **{signal.Symbol}**{downsizedNote} | {signal.SetupType} | {placedQuantity:0.###} | ${(livePrice ?? signal.CurrentPrice):F2} | £{placedCostGbp:F2} | ${stopLossPrice:F2} | ${targetPrice:F2} | {signal.ConvictionScore:F1} |");
                placed++;

                if (placed < signals.Count)
                    await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);
            }
            catch (Refit.ApiException api) when ((int)api.StatusCode is >= 400 and < 500
                && api.Content?.Contains("insufficient-free-for-stocks-buy") == true)
            {
                // Not enough free cash for THIS order right now - an account
                // condition, not a broker limit on the symbol. Cancel the
                // intent and un-claim the signal (it stays eligible for a
                // same-day re-run once cash frees up), then try the next
                // signal - a smaller position may still fit.
                trade.Status = TradeStatus.Cancelled;
                await tradeRepo.UpdateAsync(trade);
                signal.WasExecuted = false;
                await signalRepo.UpdateAsync(signal);
                await activityLog.LogAsync(accountId, "TradeEvent", "Order Rejected", "Warning",
                    $"{signal.Symbol} ({ticker}): T212 reported insufficient funds for ~£{sizing.EstimatedCost:F2} — signal stays eligible for a later run; trying the next signal.", ct);
                failedLines.Add($"**{signal.Symbol}** — T212 reported insufficient funds for ~£{sizing.EstimatedCost:F2}; the signal stays eligible for a later run");
                logger.LogWarning("Insufficient funds for {Symbol} (account {AccountId}, est £{Cost:F2}) — signal left eligible, moving on",
                    signal.Symbol, accountId, sizing.EstimatedCost);
                failed++;
                continue;
            }
            catch (Refit.ApiException api) when ((int)api.StatusCode is >= 400 and < 500 && api.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
            {
                // A 4xx is the broker actively REFUSING the order (T212
                // per-instrument position-size limits, quantity precision,
                // etc.) - nothing was placed, so there is no double-buy risk.
                // Cancel the intent, flag the signal unproceedable for the
                // rest of the day, release the cash and move on to the next
                // eligible signal in this same round.
                trade.Status = TradeStatus.Cancelled;
                await tradeRepo.UpdateAsync(trade);
                signal.BrokerRejectedAt = DateTime.UtcNow;
                await signalRepo.UpdateAsync(signal);
                await activityLog.LogAsync(accountId, "TradeEvent", "Order Rejected", "Warning",
                    $"{signal.Symbol} ({ticker}): T212 refused the order ({(int)api.StatusCode} {api.StatusCode}: {Truncate(api.Content, 160)}) — flagged unproceedable for today, moving to the next signal.", ct);
                failedLines.Add($"**{signal.Symbol}** — T212 refused the order ({(int)api.StatusCode} {api.StatusCode}); flagged unproceedable for today");
                logger.LogWarning("Broker rejected {Symbol} ({Ticker}) with {Status} for account {AccountId} — signal flagged unproceedable today",
                    signal.Symbol, ticker, api.StatusCode, accountId);
                failed++;
                continue;
            }
            catch (Exception ex)
            {
                // The order's outcome is UNKNOWN - it may have reached the broker
                // and filled. Leave the trade Pending (never delete, never
                // re-place here): Monitor's pending reconciliation resolves it
                // against T212 order history - promoting to Open if it actually
                // placed, or Cancelling it if it definitively did not.
                // Reserve its cash for the rest of THIS run too: if the order
                // did fill, later signals sized against un-decremented cash
                // would overspend. Worst case (it never placed) this run is
                // slightly conservative and reconciliation frees the capital.
                availableCash -= sizing.EstimatedCost;
                deployedThisRun += sizing.EstimatedCost;
                logger.LogError(ex, "Order placement outcome unknown for {Symbol} ({Ticker}), account {AccountId} — left as Pending for reconciliation", signal.Symbol, ticker, accountId);

                // Durable diagnosis (23 Jul 2026): telemetry from these
                // instances is unreliable and the generic activity message hid
                // WHY the broker call failed for days. Persist the broker's
                // status + response body (or the exception) where it can't get
                // lost - the dashboard activity log.
                var detail = ex switch
                {
                    Refit.ApiException api =>
                        $"T212 responded {(int)api.StatusCode} {api.StatusCode}: {Truncate(api.Content, 220)}",
                    _ => $"{ex.GetType().Name}: {Truncate(ex.Message, 220)}",
                };
                await activityLog.LogAsync(accountId, "TradeEvent", "Order Failed", "Warning",
                    $"{signal.Symbol} ({ticker}) qty={sizing.Quantity} est=£{sizing.EstimatedCost:F2} — {detail}", ct);
                failedLines.Add($"**{signal.Symbol}** — order outcome unknown ({detail}); reconciliation will confirm or cancel it within minutes");
                failed++;
            }
        }

        // Step 5 — update portfolio snapshot
        if (placed > 0)
        {
            try
            {
                // Broker investments (pre-run, GBP) plus this run's placements
                // (GBP estimated costs). The old expression summed Quantity x
                // EntryPrice over ALL open trades, double-counting positions
                // already inside the broker total - and in USD to boot.
                var openValue = openPositionsValue + deployedThisRun;
                // Deployable (active) = the un-locked share; locked capital is
                // the protected reserve (no more tier pool between them).
                var lockedCapital = totalPortfolioValue * riskProfile.LockedCapitalPct;
                var snapshot = new PortfolioSnapshot
                {
                    AccountId = accountId,
                    TradingMode = account.TradingMode,
                    SnapshotDate = date,
                    TotalCapital = totalPortfolioValue,
                    CashAvailable = availableCash,
                    OpenPositionsValue = openValue,
                    ActiveCapital = totalPortfolioValue - lockedCapital,
                    LockedCapital = lockedCapital,
                    ReserveCapital = 0,
                    TotalPnl = 0,
                };
                await portfolioRepo.AddAsync(snapshot);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist portfolio snapshot after execution for account {AccountId}", accountId);
            }
        }

        // Step 6 — send notification email if anything happened
        var symbolList = placedSymbols.Count > 0 ? $" ({string.Join(", ", placedSymbols)})" : "";
        var summary = $"{placed} placed{symbolList}, {failed} failed, {skipped} skipped";
        if (placed > 0 || failed > 0 || entrySkips.Count > 0)
        {
            try
            {
                var mdLines = new List<string>
                {
                    $"# Cadentic Execution Report — {date:dd MMM yyyy}",
                    string.Empty,
                };

                if (boughtRows.Count > 0)
                {
                    mdLines.Add($"## 🟢 Bought — {boughtRows.Count} order(s)");
                    mdLines.Add(string.Empty);
                    mdLines.Add("| Symbol | Setup | Qty | Share Price | Est. Cost | Stop | Target | Conviction |");
                    mdLines.Add("|---|---|---|---|---|---|---|---|");
                    mdLines.AddRange(boughtRows);
                    mdLines.Add(string.Empty);
                    mdLines.Add($"Deployed this run: **£{deployedThisRun:F2}**");
                }
                else
                {
                    mdLines.Add("## No orders placed");
                }
                mdLines.Add(string.Empty);

                if (failedLines.Count > 0)
                {
                    mdLines.Add($"## 🔴 Failed — {failedLines.Count}");
                    mdLines.AddRange(failedLines.Select(l => $"- {l}"));
                    mdLines.Add(string.Empty);
                }
                if (skippedLines.Count > 0)
                {
                    mdLines.Add($"## ⏭️ Skipped — {skippedLines.Count}");
                    mdLines.AddRange(skippedLines.Select(l => $"- {l}"));
                    mdLines.Add(string.Empty);
                }
                if (entrySkips.Count > 0)
                {
                    mdLines.Add("**Entries skipped by the intraday check:**");
                    mdLines.AddRange(entrySkips.Select(s => $"- {s}"));
                    mdLines.Add(string.Empty);
                }

                mdLines.Add("---");
                mdLines.Add(string.Empty);
                mdLines.Add("| Account | |");
                mdLines.Add("|---|---|");
                mdLines.Add($"| Cash remaining | **£{availableCash:F2}** |");
                mdLines.Add($"| Open positions value | £{openPositionsValue + deployedThisRun:F2} |");
                mdLines.Add($"| Portfolio total | £{totalPortfolioValue:F2} |");

                var toAddresses = (await recipients.ListAsync(accountId))
                    .Where(r => r.Categories.HasFlag(NotificationCategory.Execution))
                    .Select(r => r.Email)
                    .ToList();

                if (toAddresses.Count > 0)
                    await emailService.SendSimpleEmailAsync(
                        toAddresses,
                        string.Join(Environment.NewLine, mdLines),
                        $"Cadentic Execution — {date:dd MMM yyyy}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send execution notification email for account {AccountId}", accountId);
            }
        }

        logger.LogInformation("Account {AccountId}: {Summary}", accountId, summary);
        return new ExecutionResult(placed, failed, skipped, summary, placedSymbols);
    }

    private async Task PopulateMarketContextAsync(Trade trade, IFinnhubClient finnhub, ITiingoClient tiingo, CancellationToken ct)
    {
        // Market context is informational (feeds regime-aware refinement later) — never
        // block or fail an order placement because of it.
        try
        {
            var regime = await marketRegimeService.GetCurrentRegimeAsync(tiingo, finnhub, ct);
            trade.MarketRegimeAtEntry = regime.Regime;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not determine market regime for {Symbol} trade — leaving null", trade.Symbol);
        }

        try
        {
            var spyQuote = await finnhub.GetQuoteAsync("SPY");
            trade.SpyPriceAtEntry = spyQuote.CurrentPrice;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch SPY quote for {Symbol} trade — leaving null", trade.Symbol);
        }

        try
        {
            var vixQuote = await finnhub.GetQuoteAsync("VIX");
            trade.VixAtEntry = vixQuote.CurrentPrice;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch VIX quote for {Symbol} trade — leaving null", trade.Symbol);
        }
    }

    private async Task<string?> ResolveT212TickerAsync(int accountId, string instrumentsCacheKey, ITrading212Client t212, string symbol)
    {
        var cacheKey = $"t212_ticker_{accountId}_{symbol}";
        if (cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        // Use pre-fetched full list if available; if the pre-fetch failed, fetch now and
        // cache it so subsequent symbols in the same run don't each trigger a separate call.
        List<InstrumentResponse> instruments;
        if (cache.TryGetValue(instrumentsCacheKey, out List<InstrumentResponse>? all) && all is not null)
        {
            instruments = all;
        }
        else
        {
            instruments = await t212.GetInstrumentsAsync();
            cache.Set(instrumentsCacheKey, instruments, TimeSpan.FromHours(24));
        }

        // US listings only - see T212InstrumentResolver for the HAL/HAL Trust
        // incident this prevents. Null = ineligible; caller skips with a warning.
        var ticker = T212InstrumentResolver.ResolveUsTicker(instruments, symbol);
        cache.Set(cacheKey, ticker, TimeSpan.FromHours(24));
        return ticker;
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "(no response body)" : (text.Length <= max ? text : text[..max] + "…");
}
