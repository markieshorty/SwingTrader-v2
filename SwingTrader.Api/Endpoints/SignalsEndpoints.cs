using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Endpoints;

public static class SignalsEndpoints
{
    public static RouteGroupBuilder MapSignalsEndpoints(this RouteGroupBuilder api)
    {
        // Grouped by Recommendation to match the Angular signal board's buy/watch/hold/avoid columns.
        api.MapGet("/signals/today", async (ISignalRepository signals, IAccountContext ctx) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var todaysSignals = (await signals.GetByDateAsync(ctx.AccountId, today)).ToList();
            return Results.Ok(new
            {
                date = today,
                buy = todaysSignals.Where(s => s.Recommendation == Recommendation.Buy),
                watch = todaysSignals.Where(s => s.Recommendation == Recommendation.Watch),
                hold = todaysSignals.Where(s => s.Recommendation == Recommendation.Hold),
                avoid = todaysSignals.Where(s => s.Recommendation == Recommendation.Avoid),
            });
        });

        return api;
    }
}
