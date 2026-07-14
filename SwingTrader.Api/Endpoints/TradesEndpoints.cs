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
        // Recent trade history with derived percent/days-held/setup columns the
        // raw Trade entity doesn't carry - built by AccountViewService so the
        // admin "view a user" page shares the exact same shape.
        api.MapGet("/trades/recent", async (int? days, AccountViewService view, IAccountContext ctx, CancellationToken ct) =>
            Results.Ok(await view.GetRecentTradesAsync(ctx.AccountId, days ?? 30, ct)));

        // Open Trade rows double as "positions", enriched here with a live quote
        // (for currentPrice/unrealisedPnl) and the originating signal (for
        // setupType/convictionScoreAtEntry) - the Angular PositionDto shape needs
        // fields (stopLoss, target, daysHeld, etc.) that don't exist by those names
        // on the Trade entity itself, so this was previously returning raw Trade
        // JSON that silently didn't match what the dashboard's position cards and
        // stop-target-bar expected (blank prices/PnL/days-held in the UI).
        // Open positions enriched with a live quote + originating signal - built
        // by AccountViewService (shared with the admin per-user view).
        api.MapGet("/positions", async (AccountViewService view, IAccountContext ctx, CancellationToken ct) =>
            Results.Ok(await view.GetPositionsAsync(ctx.AccountId, ct)))
            .RequireRateLimiting(RateLimitPolicies.ExternalRead);

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

        // "Close early": the owner exits a position on demand. Delegates to
        // the SAME exit path the monitor's rule-driven exits use
        // (PositionExitService: T212 market sell against the exact
        // BrokerTicker, trade closed with an estimated P&L, exit email, and a
        // same-day Execution re-enqueue so freed capital can fund an approved
        // buy). The monitor's fill reconciliation later replaces the estimate
        // with T212's real fill price and realised P&L, exactly as it does
        // for stop/target exits.
        api.MapPost("/positions/{tradeId:int}/close-early", async (
            int tradeId,
            ITradeRepository trades,
            IUserHttpClientFactory clientFactory,
            IPositionExitService exitService,
            IActivityLogRepository activityLog,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();

            var trade = await trades.GetByIdAsync(ctx.AccountId, tradeId);
            if (trade is null) return Results.NotFound();
            if (trade.Status != TradeStatus.Open)
                return Results.BadRequest(new { message = "Only open positions can be closed." });

            ITrading212Client t212;
            try
            {
                t212 = await clientFactory.CreateTrading212Async<ITrading212Client>(ctx.AccountId, ct);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Trading212 is not available: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Estimated exit price for the immediate P&L figure (the monitor
            // reconciles the real fill afterwards); a failed quote falls back
            // to the entry price rather than blocking the close.
            var currentPrice = trade.EntryPrice;
            try
            {
                var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
                var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                if (quote.CurrentPrice is > 0) currentPrice = quote.CurrentPrice.Value;
            }
            catch { /* estimate only */ }

            var result = await exitService.ClosePositionAsync(
                ctx.AccountId, trade, t212, currentPrice,
                ExitReason.ManualClose, "Closed early by the owner from the app.", ct);

            if (!result.Success)
                return Results.Problem($"Sell order failed: {result.ErrorMessage}", statusCode: StatusCodes.Status502BadGateway);

            await activityLog.LogAsync(ctx.AccountId, "Trade", "Manual Close", "Success",
                $"{trade.Symbol}: closed early by the owner (market sell placed, est. P&L £{result.RealizedPnl:F2}).", ct);

            return Results.Ok(new { trade.Id, trade.Symbol, exitPrice = result.ExitPrice, realizedPnl = result.RealizedPnl });
        });

        return api;
    }
}
