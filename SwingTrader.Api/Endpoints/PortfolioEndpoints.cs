using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Api.Endpoints;

public static class PortfolioEndpoints
{
    public static RouteGroupBuilder MapPortfolioEndpoints(this RouteGroupBuilder api)
    {
        // Raw PortfolioSnapshot has no todayPnl/todayPnlPercent/winRate30d - the
        // Angular PortfolioDto expected these but nothing ever computed them, so
        // the Dashboard's Today P&L / 30-Day Win Rate cards always showed
        // £0.00/n-a regardless of what actually happened, not just "until sold".
        api.MapGet("/portfolio", async (
            IPortfolioRepository portfolio,
            ITradeRepository trades,
            IAccountRepository accounts,
            IUserHttpClientFactory clientFactory,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var snapshot = await portfolio.GetLatestSnapshotAsync(ctx.AccountId, account.TradingMode);
            if (snapshot is null) return Results.NotFound();

            var allTrades = (await trades.GetAllAsync(ctx.AccountId, account.TradingMode)).ToList();
            var today = DateTime.UtcNow.Date;

            var realizedToday = allTrades
                .Where(t => t.ClosedAt.HasValue && t.ClosedAt.Value.Date == today && t.RealizedPnl.HasValue)
                .Sum(t => t.RealizedPnl!.Value);

            // Today P&L = today's realized P&L + open positions' total unrealized
            // mark-to-market (current price vs entry, not strictly "since market
            // open" for positions held multiple days) - a live, meaningful number
            // rather than always zero mid-day waiting for something to close.
            var openTrades = allTrades.Where(t => t.Status == TradeStatus.Open).ToList();
            var unrealizedOpen = 0m;
            if (openTrades.Count > 0)
            {
                IFinnhubClient? finnhub = null;
                try
                {
                    finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
                }
                catch
                {
                    // No Finnhub key configured - unrealized contribution stays 0 rather than failing the whole card.
                }

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
                        catch
                        {
                            // Keep the entry-price fallback for this one symbol.
                        }
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

            return Results.Ok(new
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
            });
        }).RequireRateLimiting(RateLimitPolicies.ExternalRead);

        return api;
    }
}
