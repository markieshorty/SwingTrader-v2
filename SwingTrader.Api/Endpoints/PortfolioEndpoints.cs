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
        api.MapGet("/portfolio", async (AccountViewService view, IAccountContext ctx, CancellationToken ct) =>
        {
            var result = await view.GetPortfolioAsync(ctx.AccountId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting(RateLimitPolicies.ExternalRead);

        return api;
    }
}
