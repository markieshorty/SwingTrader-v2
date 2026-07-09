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

        return api;
    }
}
