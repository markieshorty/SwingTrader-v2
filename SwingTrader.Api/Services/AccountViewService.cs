using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;

namespace SwingTrader.Api.Services;

// Shared read-model builders for the dashboard views (portfolio card,
// positions, recent trades). Extracted so the per-account dashboard endpoints
// AND the admin "view a user's account" endpoints compute identical shapes
// from one implementation - the P&L math in particular must never diverge
// between the two. Every method is scoped by an explicit accountId (the
// caller supplies ctx.AccountId for self, or a target account for admin).
public class AccountViewService(
    IPortfolioRepository portfolio,
    ITradeRepository trades,
    ISignalRepository signals,
    IWatchlistRepository watchlist,
    IAccountRepository accounts,
    IMarketCalendarService marketCalendar,
    IForexService forex,
    IUserHttpClientFactory clientFactory)
{
    // Portfolio summary card (totals + today P&L + 30-day win rate). Null when
    // the account has no snapshot yet.
    public async Task<object?> GetPortfolioAsync(int accountId, CancellationToken ct)
    {
        var account = await accounts.GetAsync(accountId, ct);
        if (account is null) return null;

        var snapshot = await portfolio.GetLatestSnapshotAsync(accountId, account.TradingMode);
        if (snapshot is null) return null;

        var allTrades = (await trades.GetAllAsync(accountId, account.TradingMode)).ToList();
        var today = DateTime.UtcNow.Date;

        var realizedToday = allTrades
            .Where(t => t.ClosedAt.HasValue && t.ClosedAt.Value.Date == today && t.RealizedPnl.HasValue)
            .Sum(t => t.RealizedPnl!.Value);

        var openTrades = allTrades.Where(t => t.Status == TradeStatus.Open).ToList();
        var unrealizedOpen = 0m;
        if (openTrades.Count > 0)
        {
            IFinnhubClient? finnhub = null;
            try { finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(accountId, ct); }
            catch { /* no Finnhub key - unrealized contribution stays 0 rather than failing the card */ }

            foreach (var trade in openTrades)
            {
                var currentPrice = trade.EntryPrice;
                if (finnhub is not null)
                {
                    try
                    {
                        var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                        if (quote.CurrentPrice is > 0) currentPrice = quote.CurrentPrice.Value;
                    }
                    catch { /* keep the entry-price fallback for this one symbol */ }
                }
                unrealizedOpen += (currentPrice - trade.EntryPrice) * trade.Quantity;
            }
        }

        var todayPnl = realizedToday + unrealizedOpen;
        var todayPnlPercent = snapshot.TotalCapital > 0 ? todayPnl / snapshot.TotalCapital * 100m : 0m;

        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var closedLast30Days = allTrades
            .Where(t => t.ClosedAt.HasValue && t.ClosedAt.Value >= thirtyDaysAgo && t.RealizedPnl.HasValue)
            .ToList();
        var winRate30d = closedLast30Days.Count > 0
            ? (decimal)closedLast30Days.Count(t => t.RealizedPnl > 0) / closedLast30Days.Count * 100m
            : 0m;

        return new
        {
            snapshot.TotalCapital,
            snapshot.LockedCapital,
            snapshot.ReserveCapital,
            snapshot.ActiveCapital,
            snapshot.CashAvailable,
            snapshot.OpenPositionsValue,
            snapshot.TotalPnl,
            TodayPnl = todayPnl,
            TodayPnlPercent = todayPnlPercent,
            WinRate30d = winRate30d,
            snapshot.CurrentTier,
        };
    }

    // Open positions, enriched with a live quote and originating signal.
    public async Task<IReadOnlyList<object>> GetPositionsAsync(int accountId, CancellationToken ct)
    {
        var account = await accounts.GetAsync(accountId, ct);
        if (account is null) return [];

        var openTrades = (await trades.GetOpenTradesAsync(accountId, account.TradingMode)).ToList();
        if (openTrades.Count == 0) return [];

        IFinnhubClient? finnhub = null;
        try { finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(accountId, ct); }
        catch { /* no Finnhub key - fall back to entry price rather than failing the list */ }

        // For "actual money" at stop/current/target: one GBP-per-USD rate for
        // the whole list (cached 60 min, falls back to a default, never
        // throws). Where the trade recorded its REAL entry cash, the
        // effective per-trade rate (EntryValueGbp / entry USD value) is used
        // instead - it bakes in the actual FX conversion T212 applied.
        var gbpPerUsd = await forex.GetGbpUsdRateAsync(ct);

        var results = new List<object>(openTrades.Count);
        foreach (var trade in openTrades)
        {
            var currentPrice = trade.EntryPrice;
            if (finnhub is not null)
            {
                try
                {
                    var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                    if (quote.CurrentPrice is > 0) currentPrice = quote.CurrentPrice.Value;
                }
                catch { /* one symbol's quote failure shouldn't blank the whole list */ }
            }

            var unrealisedPnl = (currentPrice - trade.EntryPrice) * trade.Quantity;
            var unrealisedPnlPercent = trade.EntryPrice > 0 ? (currentPrice - trade.EntryPrice) / trade.EntryPrice * 100m : 0m;
            // Trading days held (weekends/holidays excluded) so the displayed
            // "Day N" lines up with the trading-day-based probation and
            // time-exit schedule - a calendar count would show "Day 3" while
            // the probation check still sees it as day 1.
            var daysHeld = marketCalendar.TradingDaysBetween(
                DateOnly.FromDateTime(trade.OpenedAt), DateOnly.FromDateTime(DateTime.UtcNow));

            var signal = trade.SignalId.HasValue ? await signals.GetByIdAsync(accountId, trade.SignalId.Value) : null;

            var isNearStop = trade.StopLossPrice > 0 && Math.Abs(currentPrice - trade.StopLossPrice) / trade.StopLossPrice <= 0.02m;
            var isNearTarget = trade.TargetPrice > 0 && Math.Abs(currentPrice - trade.TargetPrice) / trade.TargetPrice <= 0.02m;

            var companyName = trade.CompanyName
                ?? (await watchlist.GetBySymbolAsync(accountId, trade.Symbol))?.CompanyName
                ?? trade.Symbol;

            var entryUsdValue = trade.EntryPrice * trade.Quantity;
            var fx = trade.EntryValueGbp is > 0 && entryUsdValue > 0
                ? trade.EntryValueGbp.Value / entryUsdValue
                : gbpPerUsd;

            results.Add(new
            {
                trade.Id,
                trade.Symbol,
                CompanyName = companyName,
                trade.EntryPrice,
                CurrentPrice = currentPrice,
                StopLoss = trade.StopLossPrice,
                Target = trade.TargetPrice,
                trade.TrailingStopPrice,
                trade.Quantity,
                // "Actual money" - share prices times the position size,
                // converted at the trade's own entry FX rate where known
                // (falls back to the current market rate).
                StopLossValueGbp = trade.StopLossPrice * trade.Quantity * fx,
                CurrentValueGbp = currentPrice * trade.Quantity * fx,
                TargetValueGbp = trade.TargetPrice * trade.Quantity * fx,
                TrailingStopValueGbp = trade.TrailingStopPrice.HasValue ? trade.TrailingStopPrice.Value * trade.Quantity * fx : (decimal?)null,
                UnrealisedPnl = unrealisedPnl,
                UnrealisedPnlPercent = unrealisedPnlPercent,
                DaysHeld = daysHeld,
                EntryDate = trade.OpenedAt,
                SetupType = signal?.SetupType.ToString() ?? "Unknown",
                ConvictionScoreAtEntry = signal?.ConvictionScore,
                trade.MarketRegimeAtEntry,
                IsNearStop = isNearStop,
                IsNearTarget = isNearTarget,
                trade.Phase,
                trade.MomentumHealthScore,
                trade.MomentumHealthVerdict,
                trade.MomentumHealthReasoning,
                trade.MomentumHealthCheckedAt,
                trade.PhaseConfirmedAt,
                // The contract this position runs under - rules frozen at buy
                // time (null = pre-freeze trade, UI falls back to "profile").
                trade.MaxHoldDaysAtEntry,
                trade.MinHoldDaysAtEntry,
                trade.MomentumHealthThresholdAtEntry,
                trade.TrailingActivationPctAtEntry,
                trade.TrailingDistancePctAtEntry,
                trade.ForwardScoreAtEntry,
                trade.SizeMultiplier,
            });
        }

        return results;
    }

    // Recent trade history, with derived RealizedPnlPercent/DaysHeld/SetupType/
    // ConvictionScoreAtEntry the raw Trade entity doesn't carry.
    public async Task<IReadOnlyList<object>> GetRecentTradesAsync(int accountId, int days, CancellationToken ct)
    {
        var account = await accounts.GetAsync(accountId, ct);
        if (account is null) return [];

        var from = DateTime.UtcNow.AddDays(-days);
        // Exclude intent-first placement states: a Pending intent isn't a
        // confirmed position yet, and a Cancelled one never reached the broker -
        // neither belongs in the user's trade history.
        var history = (await trades.GetTradeHistoryAsync(accountId, account.TradingMode, from, DateTime.UtcNow))
            .Where(t => t.Status != TradeStatus.Pending && t.Status != TradeStatus.Cancelled);

        var results = new List<object>();
        foreach (var trade in history)
        {
            var end = trade.ClosedAt ?? DateTime.UtcNow;
            var daysHeld = Math.Max(0, (int)(end - trade.OpenedAt).TotalDays);

            var realizedPnlPercent = trade.RealizedPnl.HasValue && trade.EntryValueGbp is > 0
                ? trade.RealizedPnl.Value / trade.EntryValueGbp.Value * 100m
                : trade.ExitPrice.HasValue && trade.EntryPrice > 0
                    ? (trade.ExitPrice.Value - trade.EntryPrice) / trade.EntryPrice * 100m
                    : (decimal?)null;

            var signal = trade.SignalId.HasValue ? await signals.GetByIdAsync(accountId, trade.SignalId.Value) : null;
            var totalFeesGbp = trade.EntryFeesGbp.HasValue || trade.ExitFeesGbp.HasValue
                ? (trade.EntryFeesGbp ?? 0m) + (trade.ExitFeesGbp ?? 0m)
                : (decimal?)null;

            results.Add(new
            {
                trade.Id,
                trade.Symbol,
                trade.CompanyName,
                Direction = trade.Direction.ToString(),
                trade.EntryPrice,
                trade.ExitPrice,
                trade.EntryValueGbp,
                trade.ExitValueGbp,
                FeesGbp = totalFeesGbp,
                trade.RealizedPnl,
                RealizedPnlPercent = realizedPnlPercent,
                DaysHeld = daysHeld,
                Status = trade.Status.ToString(),
                SetupType = signal?.SetupType.ToString() ?? "Unknown",
                ConvictionScoreAtEntry = signal?.ConvictionScore,
                trade.MarketRegimeAtEntry,
                OpenedAt = trade.OpenedAt,
                trade.ClosedAt,
                trade.MaxHoldDaysAtEntry,
                trade.MinHoldDaysAtEntry,
                trade.MomentumHealthThresholdAtEntry,
                trade.TrailingActivationPctAtEntry,
                trade.TrailingDistancePctAtEntry,
                trade.ForwardScoreAtEntry,
                trade.SizeMultiplier,
            });
        }

        return results;
    }
}
