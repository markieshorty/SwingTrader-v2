using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

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
            IBacktestRunRepository runs,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var sum = req.Weights.Rsi + req.Weights.Macd + req.Weights.Volume + req.Weights.Sentiment
                + req.Weights.SetupQuality + req.Weights.RelativeStrength + req.Weights.PriceLevel + req.Weights.FundamentalMomentum;
            if (Math.Abs(sum - 1.0m) > 0.01m)
                return Results.BadRequest(new { message = $"Weights must sum to 1.0 (currently {sum:F2})." });

            if (req.DataSource.Equals("historic", StringComparison.OrdinalIgnoreCase))
            {
                // Historic runs take minutes (full engine over ~1M bars) - they
                // execute as a Service Bus job; the UI polls the run row.
                if (serviceBus is null)
                    return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

                var historicRequest = new HistoricBacktestRequest(
                    new HistoricBacktestWeights(
                        req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume, req.Weights.Sentiment,
                        req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel, req.Weights.FundamentalMomentum),
                    req.BuyThreshold, req.ExcludeBreakout);

                var run = await runs.AddAsync(new BacktestRun
                {
                    AccountId = ctx.AccountId,
                    RequestJson = JsonSerializer.Serialize(historicRequest),
                });

                await using var sender = serviceBus.CreateSender("backtest-jobs");
                await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                    new BacktestJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), run.Id))), ct);

                return Results.Ok(new { backtestRunId = run.Id });
            }

            var response = await lab.RunOwnDataAsync(ctx.AccountId, req, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        // Poll a historic run. Result JSON is the shared HistoricResult shape.
        api.MapGet("/strategy-lab/backtest/{id:int}", async (
            int id, IBacktestRunRepository runs, IAccountContext ctx) =>
        {
            var run = await runs.GetByIdAsync(ctx.AccountId, id);
            return run is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    run.Id,
                    run.Status,
                    run.Error,
                    run.StartedAt,
                    run.CompletedAt,
                    result = run.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(run.ResultJson),
                });
        });

        // Availability of the shared historic dataset, so the UI can enable/
        // disable the historic option and show data freshness.
        api.MapGet("/strategy-lab/data-status", async (
            IHistoricalCandleRepository candles, IConfiguration config, CancellationToken ct) =>
            Results.Ok(new
            {
                bars = await candles.CountAsync(ct),
                latestDate = await candles.GetMaxDateAsync(ct),
                platformKeyConfigured = !string.IsNullOrWhiteSpace(config["Tiingo:PlatformApiKey"]),
            }));

        // Owner-only manual candle sync trigger (also runs weekly via the
        // scheduler). Enqueues the platform-level job.
        api.MapPost("/strategy-lab/sync-data", async (
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            await using var sender = serviceBus.CreateSender("candlesync-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new CandleSyncJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N")))), ct);
            return Results.Ok(new { queued = true });
        });

        return api;
    }
}
