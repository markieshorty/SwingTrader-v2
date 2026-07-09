using SwingTrader.Agents.Monitor;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Api.Endpoints;

public static class TradesEndpoints
{
    public static RouteGroupBuilder MapTradesEndpoints(this RouteGroupBuilder api)
    {
        // Raw Trade JSON doesn't carry RealizedPnlPercent/DaysHeld/SetupType/
        // ConvictionScoreAtEntry - those aren't columns on the entity, they're
        // derived the same way /positions already derives its equivalents below.
        // Was previously returning the bare entity, silently leaving the Angular
        // TradeDto's percent/days-held/setup columns blank for every closed trade.
        api.MapGet("/trades/recent", async (int? days, ITradeRepository trades, ISignalRepository signals, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var from = DateTime.UtcNow.AddDays(-(days ?? 30));
            var history = await trades.GetTradeHistoryAsync(ctx.AccountId, account.TradingMode, from, DateTime.UtcNow);

            var results = new List<object>();
            foreach (var trade in history)
            {
                var end = trade.ClosedAt ?? DateTime.UtcNow;
                var daysHeld = Math.Max(0, (int)(end - trade.OpenedAt).TotalDays);

                // T212's own RealizedPnl/EntryValueGbp (both real £, post-FX/fees -
                // see MonitorService.ReconcileOrderFillsAsync) are the source of
                // truth once the fill is confirmed. Falls back to a per-share-price
                // estimate only for trades not yet reconciled (or from before this
                // feature existed), which understates/overstates return% by
                // whatever the FX/fee difference was - real but temporary.
                var realizedPnlPercent = trade.RealizedPnl.HasValue && trade.EntryValueGbp is > 0
                    ? trade.RealizedPnl.Value / trade.EntryValueGbp.Value * 100m
                    : trade.ExitPrice.HasValue && trade.EntryPrice > 0
                        ? (trade.ExitPrice.Value - trade.EntryPrice) / trade.EntryPrice * 100m
                        : (decimal?)null;

                var signal = trade.SignalId.HasValue ? await signals.GetByIdAsync(ctx.AccountId, trade.SignalId.Value) : null;
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
                });
            }

            return Results.Ok(results);
        });

        // Open Trade rows double as "positions", enriched here with a live quote
        // (for currentPrice/unrealisedPnl) and the originating signal (for
        // setupType/convictionScoreAtEntry) - the Angular PositionDto shape needs
        // fields (stopLoss, target, daysHeld, etc.) that don't exist by those names
        // on the Trade entity itself, so this was previously returning raw Trade
        // JSON that silently didn't match what the dashboard's position cards and
        // stop-target-bar expected (blank prices/PnL/days-held in the UI).
        api.MapGet("/positions", async (
            ITradeRepository trades,
            ISignalRepository signals,
            IWatchlistRepository watchlist,
            IAccountRepository accounts,
            IUserHttpClientFactory clientFactory,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var openTrades = (await trades.GetOpenTradesAsync(ctx.AccountId, account.TradingMode)).ToList();
            if (openTrades.Count == 0) return Results.Ok(Array.Empty<object>());

            IFinnhubClient? finnhub = null;
            try
            {
                finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
            }
            catch
            {
                // No Finnhub key configured - fall back to entry price below rather
                // than failing the whole positions list.
            }

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
                    catch
                    {
                        // Keep the entry-price fallback - one symbol's quote failure
                        // shouldn't blank out the whole positions list.
                    }
                }

                var unrealisedPnl = (currentPrice - trade.EntryPrice) * trade.Quantity;
                var unrealisedPnlPercent = trade.EntryPrice > 0 ? (currentPrice - trade.EntryPrice) / trade.EntryPrice * 100m : 0m;
                var daysHeld = Math.Max(0, (int)(DateTime.UtcNow - trade.OpenedAt).TotalDays);

                var signal = trade.SignalId.HasValue ? await signals.GetByIdAsync(ctx.AccountId, trade.SignalId.Value) : null;

                // "Near" the stop/target = within 2% of that boundary price - close
                // enough to flag on the dashboard without being a hard trigger
                // (MonitorService owns the actual stop/target exit logic).
                var isNearStop = trade.StopLossPrice > 0 && Math.Abs(currentPrice - trade.StopLossPrice) / trade.StopLossPrice <= 0.02m;
                var isNearTarget = trade.TargetPrice > 0 && Math.Abs(currentPrice - trade.TargetPrice) / trade.TargetPrice <= 0.02m;

                // Older trades placed before Trade.CompanyName existed fall back to a
                // live watchlist lookup, then the bare symbol if it's since been removed.
                var companyName = trade.CompanyName
                    ?? (await watchlist.GetBySymbolAsync(ctx.AccountId, trade.Symbol))?.CompanyName
                    ?? trade.Symbol;

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
                });
            }

            return Results.Ok(results);
        }).RequireRateLimiting(RateLimitPolicies.ExternalRead);

        // Manual/admin trigger — recompute momentum health for a single open
        // position outside the normal MinHoldDays schedule. Useful for support and
        // for verifying a probation configuration change without waiting a day.
        api.MapPost("/positions/{tradeId:int}/check-momentum", async (
            int tradeId,
            ITradeRepository trades,
            IMomentumHealthService momentumHealth,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var trade = await trades.GetByIdAsync(ctx.AccountId, tradeId);
            if (trade is null) return Results.NotFound();

            var result = await momentumHealth.CalculateAsync(ctx.AccountId, trade, ct);
            return Results.Ok(result);
        }).RequireRateLimiting(RateLimitPolicies.ExternalRead);

        return api;
    }
}
