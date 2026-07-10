using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Endpoints;

public static class StrategyLabEndpoints
{
    public static RouteGroupBuilder MapStrategyLabEndpoints(this RouteGroupBuilder api)
    {
        // Read-only simulation - any member can experiment. Applying dials to
        // production goes through the existing PUT /strategy-weights, which
        // enforces Owner + sum-to-1.0 validation.
        api.MapPost("/strategy-lab/run", async (
            StrategyLabRequest req,
            StrategyLabService lab,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (req.DataSource.Equals("historic", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new
                {
                    message = "Historic market-data simulation isn't available in the app yet — it runs via the local backtester. Own-data replay is available now.",
                });

            var sum = req.Weights.Rsi + req.Weights.Macd + req.Weights.Volume + req.Weights.Sentiment
                + req.Weights.SetupQuality + req.Weights.RelativeStrength + req.Weights.PriceLevel + req.Weights.FundamentalMomentum;
            if (Math.Abs(sum - 1.0m) > 0.01m)
                return Results.BadRequest(new { message = $"Weights must sum to 1.0 (currently {sum:F2})." });

            var response = await lab.RunOwnDataAsync(ctx.AccountId, req, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        return api;
    }
}
