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
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Execution;

public class ExecutionService(
    ISignalRepository signalRepo,
    ITradeRepository tradeRepo,
    IPortfolioRepository portfolioRepo,
    IApprovalRepository approvalRepo,
    IAccountRepository accountRepo,
    IPositionSizingService sizingService,
    IAccountRiskProfileRepository riskProfileRepo,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    IMemoryCache cache,
    IForexService forex,
    IMarketRegimeService marketRegimeService,
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

        HashSet<string>? approvedSymbols = null;
        if (account.ApprovalRequired)
        {
            var approval = await approvalRepo.GetByDateAsync(accountId, date);
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
        var allSignals = (await signalRepo.GetByDateAsync(accountId, date))
            .Where(s => s.Recommendation == Recommendation.Buy && !s.WasExecuted)
            .OrderByDescending(s => s.ConvictionScore)
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
        var openTrades = (await tradeRepo.GetOpenTradesAsync(accountId)).ToList();

        // GBP per USD. Available cash / portfolio value are GBP (T212 base), but the
        // signal price is USD — convert it before sizing so the budget (GBP) and the
        // price share a currency and the quantity isn't off by ~the FX rate.
        var gbpUsd = await forex.GetGbpUsdRateAsync(ct);

        // TotalValue/Investments.CurrentValue are already in the account's
        // base currency (GBP), computed by T212 itself.
        var openPositionsValue = accountSummary.Investments.CurrentValue;
        var totalPortfolioValue = accountSummary.TotalValue;
        var latestSnapshot = await portfolioRepo.GetLatestSnapshotAsync(accountId);
        var currentTier = latestSnapshot?.CurrentTier ?? CapitalTier.Tier1;

        logger.LogInformation(
            "Execution starting for account {AccountId}: {Date} | Cash={Cash:F2} | OpenPositionsValue={Positions:F2} | TotalPortfolio={Portfolio:F2} | Signals={Count}",
            accountId, date, availableCash, openPositionsValue, totalPortfolioValue, signals.Count);

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
                skipped++;
                continue;
            }

            var sizing = await sizingService.CalculateAsync(
                signal, currentTier, openTrades.Count, availableCash, totalPortfolioValue, riskProfile,
                priceOverride: signal.CurrentPrice * gbpUsd);

            if (!sizing.CanTrade)
            {
                logger.LogInformation("Skipping {Symbol}: {Reason} (account {AccountId})", signal.Symbol, sizing.RejectionReason, accountId);
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
                failed++;
                continue;
            }

            if (ticker is null)
            {
                logger.LogWarning("No T212 instrument found for {Symbol} (account {AccountId}) — skipping", signal.Symbol, accountId);
                skipped++;
                continue;
            }

            try
            {
                var order = await t212.PlaceMarketOrderAsync(
                    new MarketOrderRequest(ticker, sizing.Quantity));

                logger.LogInformation(
                    "Order placed for account {AccountId}: {Symbol} ({Ticker}) qty={Qty} estimatedCost={Cost:F2} orderId={OrderId}",
                    accountId, signal.Symbol, ticker, sizing.Quantity, sizing.EstimatedCost, order.Id);

                var trade = new Trade
                {
                    AccountId = accountId,
                    Symbol = signal.Symbol,
                    Direction = TradeDirection.Long,
                    EntryPrice = signal.CurrentPrice,
                    Quantity = sizing.Quantity,
                    StopLossPrice = signal.CalculatedStopLoss ?? signal.CurrentPrice * 0.95m,
                    TargetPrice = signal.CalculatedTarget ?? signal.CurrentPrice * 1.08m,
                    Status = TradeStatus.Open,
                    EntryOrderId = order.Id.ToString(),
                    OpenedAt = DateTime.UtcNow,
                    SignalId = signal.Id,
                };
                await PopulateMarketContextAsync(trade, finnhub, tiingo, ct);
                await tradeRepo.AddAsync(trade);
                openTrades.Add(trade);

                signal.WasExecuted = true;
                await signalRepo.UpdateAsync(signal);

                availableCash -= sizing.EstimatedCost;
                placedSymbols.Add(signal.Symbol);
                placed++;

                if (placed < signals.Count)
                    await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order failed for {Symbol} ({Ticker}), account {AccountId}", signal.Symbol, ticker, accountId);
                failed++;
            }
        }

        // Step 5 — update portfolio snapshot
        if (placed > 0)
        {
            try
            {
                var activeCapitalPct = currentTier switch
                {
                    CapitalTier.Tier1 => Core.Constants.CapitalRules.Tier1CapitalPct,
                    CapitalTier.Tier2 => Core.Constants.CapitalRules.Tier2CapitalPct,
                    CapitalTier.Tier3 => Core.Constants.CapitalRules.Tier3CapitalPct,
                    _ => Core.Constants.CapitalRules.Tier1CapitalPct
                };
                var openValue = openPositionsValue + openTrades.Where(t => t.EntryOrderId != null).Sum(t => t.Quantity * t.EntryPrice);
                var snapshot = new PortfolioSnapshot
                {
                    AccountId = accountId,
                    SnapshotDate = date,
                    TotalCapital = totalPortfolioValue,
                    CashAvailable = availableCash,
                    OpenPositionsValue = openValue,
                    ActiveCapital = totalPortfolioValue * activeCapitalPct,
                    LockedCapital = totalPortfolioValue * riskProfile.LockedCapitalPct,
                    ReserveCapital = totalPortfolioValue * (1 - activeCapitalPct - riskProfile.LockedCapitalPct),
                    TotalPnl = 0,
                    CurrentTier = currentTier,
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
        if (placed > 0 || failed > 0)
        {
            try
            {
                var mdLines = new List<string>
                {
                    $"# SwingTrader Execution Report — {date:dd MMM yyyy}",
                    string.Empty,
                    $"| | Count |",
                    $"|---|---|",
                    $"| Orders placed | **{placed}** |",
                    $"| Orders failed | **{failed}** |",
                    $"| Signals skipped | **{skipped}** |",
                    string.Empty,
                    $"Cash remaining: **£{availableCash:F2}**"
                };

                var toAddresses = (await recipients.ListAsync(accountId))
                    .Where(r => r.Categories.HasFlag(NotificationCategory.Execution))
                    .Select(r => r.Email)
                    .ToList();

                if (toAddresses.Count > 0)
                    await emailService.SendSimpleEmailAsync(
                        toAddresses,
                        string.Join(Environment.NewLine, mdLines),
                        $"SwingTrader Execution — {date:dd MMM yyyy}");
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

        // Use pre-fetched full list if available, otherwise fetch (may 429 if called rapidly)
        var instruments = cache.TryGetValue(instrumentsCacheKey, out List<InstrumentResponse>? all) && all is not null
            ? all
            : await t212.GetInstrumentsAsync();

        var match = instruments.FirstOrDefault(i =>
            i.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase)
            || i.Ticker.StartsWith(symbol + "_", StringComparison.OrdinalIgnoreCase)
            || i.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        var ticker = match?.Ticker;
        cache.Set(cacheKey, ticker, TimeSpan.FromHours(24));
        return ticker;
    }
}
