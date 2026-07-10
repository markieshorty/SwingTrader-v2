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
            IStrategyWeightsRepository weightsRepo,
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

                var userWeights = new HistoricBacktestWeights(
                    req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume, req.Weights.Sentiment,
                    req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel, req.Weights.FundamentalMomentum);

                HistoricBacktestRequest historicRequest;
                if (req.CompareBaseline)
                {
                    // A/B: snapshot the production dials into the request NOW,
                    // so the comparison is labelled with what was actually
                    // evaluated even if production changes mid-run.
                    var baseline = await SnapshotBaselineAsync(weightsRepo, ctx.AccountId);
                    if (baseline is null)
                        return Results.BadRequest(new { message = "No active production weights found to compare against." });
                    historicRequest = new HistoricBacktestRequest(
                        userWeights, req.BuyThreshold, req.ExcludeBreakout,
                        Mode: "ab",
                        Candidates:
                        [
                            new HistoricBacktestCandidate("Your dials", userWeights, req.BuyThreshold, req.ExcludeBreakout),
                            baseline,
                        ]);
                }
                else
                {
                    historicRequest = new HistoricBacktestRequest(userWeights, req.BuyThreshold, req.ExcludeBreakout);
                }

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

        // Optimizer sweep: evaluates a capped set of dial candidates around the
        // production baseline on a train window, validates the winner on the
        // held-out remainder. Long job (roughly 25 engine runs) - queued like
        // any historic run and polled via the same endpoint.
        api.MapPost("/strategy-lab/optimize", async (
            IBacktestRunRepository runs,
            IStrategyWeightsRepository weightsRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var baseline = await SnapshotBaselineAsync(weightsRepo, ctx.AccountId);
            if (baseline is null)
                return Results.BadRequest(new { message = "No active production weights found to optimize around." });

            var run = await runs.AddAsync(new BacktestRun
            {
                AccountId = ctx.AccountId,
                RequestJson = JsonSerializer.Serialize(new HistoricBacktestRequest(
                    baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                    Mode: "sweep",
                    Candidates: [baseline])),
            });

            await using var sender = serviceBus.CreateSender("backtest-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new BacktestJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), run.Id))), ct);

            return Results.Ok(new { backtestRunId = run.Id });
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

        // "Analyse this run": Claude reads a completed run and suggests a next
        // config worth TESTING. Advisory only - the user loads the suggestion
        // and runs it themselves. Rate-limited like the other Claude-backed
        // endpoints since every call costs an API request.
        api.MapPost("/strategy-lab/analyse", async (
            LabAnalyseRequest req,
            StrategyLabAnalysisService analysis,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var (response, error) = await analysis.AnalyseAsync(ctx.AccountId, req, ct);
            return error is not null
                ? Results.BadRequest(new { message = error })
                : Results.Ok(response);
        }).RequireRateLimiting(RateLimitPolicies.ClaudeJobs);

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

    // The production dials as a labelled candidate, captured at queue time.
    private static async Task<HistoricBacktestCandidate?> SnapshotBaselineAsync(
        IStrategyWeightsRepository weightsRepo, int accountId)
    {
        var prod = await weightsRepo.GetActiveWeightsAsync(accountId);
        if (prod is null) return null;
        return new HistoricBacktestCandidate(
            "Production baseline",
            new HistoricBacktestWeights(
                prod.RsiWeight, prod.MacdWeight, prod.VolumeWeight, prod.SentimentWeight,
                prod.SetupQualityWeight, prod.RelativeStrengthWeight, prod.PriceLevelWeight, prod.FundamentalMomentumWeight),
            prod.BuyThreshold,
            // Production policy: Breakout setups are hard-capped at Watch in
            // ResearchPipeline, so the baseline replays with them excluded.
            ExcludeBreakout: true);
    }
}
