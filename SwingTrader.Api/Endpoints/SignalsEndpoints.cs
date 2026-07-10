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

        // Every signal ever scored for the account, most recent first - backs
        // the "View historic signals" toggle on the Today's Signals page.
        // ExecutionService and /signals/today only ever query GetByDateAsync
        // for a single day, so nothing here is ever eligible to be bought;
        // this is a read-only history view.
        api.MapGet("/signals/history", async (ISignalRepository signals, IAccountContext ctx) =>
        {
            var all = (await signals.GetAllAsync(ctx.AccountId))
                .OrderByDescending(s => s.SignalDate)
                .ThenBy(s => s.Symbol)
                .ToList();
            return Results.Ok(new
            {
                buy = all.Where(s => s.Recommendation == Recommendation.Buy),
                watch = all.Where(s => s.Recommendation == Recommendation.Watch),
                hold = all.Where(s => s.Recommendation == Recommendation.Hold),
                avoid = all.Where(s => s.Recommendation == Recommendation.Avoid),
            });
        });

        return api;
    }
}
