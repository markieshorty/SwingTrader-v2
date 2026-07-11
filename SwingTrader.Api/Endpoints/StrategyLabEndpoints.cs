using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc;
using SwingTrader.Agents.Refinement;
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
            IAccountRiskProfileRepository riskProfileRepo,
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
                    // evaluated even if production changes mid-run. The
                    // baseline runs with the ACCOUNT's autopause setting; the
                    // user's column runs with the checkbox value.
                    var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct);
                    if (baseline is null)
                        return Results.BadRequest(new { message = "No active production weights found to compare against." });
                    historicRequest = new HistoricBacktestRequest(
                        userWeights, req.BuyThreshold, req.ExcludeBreakout,
                        Mode: "ab",
                        Candidates:
                        [
                            // Rule overrides apply to the user's side only —
                            // the baseline replays with the account's live
                            // risk-profile rules, so the comparison isolates
                            // exactly what the user changed.
                            new HistoricBacktestCandidate("Your dials", userWeights, req.BuyThreshold, req.ExcludeBreakout, req.AutopauseDuringBear, req.Rules),
                            baseline,
                        ]);
                }
                else
                {
                    historicRequest = new HistoricBacktestRequest(
                        userWeights, req.BuyThreshold, req.ExcludeBreakout,
                        AutopauseDuringBear: req.AutopauseDuringBear,
                        Rules: req.Rules);
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

        // Optimizer sweep: evaluates TWO candidate pools around the production
        // baseline on a train window - a deterministic/random sweep (see
        // SweepOptimizer.TargetCandidateCount) plus a multi-start CMA-ES
        // search (see MlSweepOptimizer) - then validates the overall winner
        // on the held-out remainder. Long job (~1.5-2 hours) - queued like
        // any historic run and polled via the same endpoint, which also
        // reports CompletedCandidates/TotalCandidates for a progress bar.
        api.MapPost("/strategy-lab/optimize", async (
            IBacktestRunRepository runs,
            IStrategyWeightsRepository weightsRepo,
            IAccountRiskProfileRepository riskProfileRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct);
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

        // Out-of-sample validation of the CURRENT form dials+rules: the
        // optimizer's train/holdout split + hold-up verdict, on demand. This
        // exists because hand-tuned configs are in-sample by construction -
        // the user iterated against the full window - and the A/B card alone
        // can't tell a real edge from a curve fit.
        api.MapPost("/strategy-lab/validate", async (
            StrategyLabRequest req,
            IBacktestRunRepository runs,
            IStrategyWeightsRepository weightsRepo,
            IAccountRiskProfileRepository riskProfileRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var userWeights = new HistoricBacktestWeights(
                req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume, req.Weights.Sentiment,
                req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel, req.Weights.FundamentalMomentum);
            var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct);
            if (baseline is null)
                return Results.BadRequest(new { message = "No active production weights found to validate against." });

            var run = await runs.AddAsync(new BacktestRun
            {
                AccountId = ctx.AccountId,
                RequestJson = JsonSerializer.Serialize(new HistoricBacktestRequest(
                    userWeights, req.BuyThreshold, req.ExcludeBreakout,
                    Mode: "validate",
                    Candidates:
                    [
                        new HistoricBacktestCandidate("Your dials", userWeights, req.BuyThreshold, req.ExcludeBreakout, req.AutopauseDuringBear, req.Rules),
                        baseline,
                    ])),
            });

            await using var sender = serviceBus.CreateSender("backtest-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new BacktestJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), run.Id))), ct);

            return Results.Ok(new { backtestRunId = run.Id });
        });

        // Monte Carlo robustness check: full-window run of the CURRENT form
        // dials+rules, then bootstrap-resample its trade log to separate
        // trade QUALITY from trade-ORDER luck. Complements /validate (which
        // measures period luck via the train/holdout split).
        api.MapPost("/strategy-lab/montecarlo", async (
            StrategyLabRequest req,
            IBacktestRunRepository runs,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var userWeights = new HistoricBacktestWeights(
                req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume, req.Weights.Sentiment,
                req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel, req.Weights.FundamentalMomentum);

            var run = await runs.AddAsync(new BacktestRun
            {
                AccountId = ctx.AccountId,
                RequestJson = JsonSerializer.Serialize(new HistoricBacktestRequest(
                    userWeights, req.BuyThreshold, req.ExcludeBreakout,
                    Mode: "montecarlo",
                    AutopauseDuringBear: req.AutopauseDuringBear,
                    Rules: req.Rules)),
            });

            await using var sender = serviceBus.CreateSender("backtest-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new BacktestJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), run.Id))), ct);

            return Results.Ok(new { backtestRunId = run.Id });
        });

        // Latest completed run of a given mode - lets the UI restore the most
        // recent optimizer result on tab load instead of losing an hour-long
        // run's output the moment the page refreshes (results were always
        // persisted; only the run id lived in component memory).
        api.MapGet("/strategy-lab/backtest/latest", async (
            string mode, IBacktestRunRepository runs, IAccountContext ctx, CancellationToken ct) =>
        {
            var run = await runs.GetLatestCompletedByModeAsync(ctx.AccountId, mode, ct);
            return run is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    run.Id,
                    run.Status,
                    run.Error,
                    run.StartedAt,
                    run.CompletedAt,
                    run.TotalCandidates,
                    run.CompletedCandidates,
                    result = run.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(run.ResultJson),
                });
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
                    run.TotalCandidates,
                    run.CompletedCandidates,
                    result = run.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(run.ResultJson),
                });
        });

        // Apply dials to production from the Lab - one click, but with the
        // same audit trail as the refinement page's Approve: records a
        // RefinementSuggestion (Origin = StrategyLab, with the run's evidence)
        // and immediately applies it through ApplyRefinementService, so the
        // refinement history shows every production weight change and where
        // it came from.
        api.MapPost("/strategy-lab/apply", async (
            LabApplyRequest req,
            IStrategyWeightsRepository weightsRepo,
            IRefinementSuggestionRepository suggestionRepo,
            IApplyRefinementService applyService,
            IAccountRepository accounts,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var sum = req.Weights.Rsi + req.Weights.Macd + req.Weights.Volume + req.Weights.Sentiment
                + req.Weights.SetupQuality + req.Weights.RelativeStrength + req.Weights.PriceLevel + req.Weights.FundamentalMomentum;
            if (Math.Abs(sum - 1.0m) > 0.01m)
                return Results.BadRequest(new { message = $"Weights must sum to 1.0 (currently {sum:F2})." });

            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var current = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
            if (current is null)
                return Results.BadRequest(new { message = "No active production weights found." });

            var suggested = new StrategyWeights
            {
                RsiWeight = req.Weights.Rsi, MacdWeight = req.Weights.Macd,
                VolumeWeight = req.Weights.Volume, SentimentWeight = req.Weights.Sentiment,
                SetupQualityWeight = req.Weights.SetupQuality, RelativeStrengthWeight = req.Weights.RelativeStrength,
                PriceLevelWeight = req.Weights.PriceLevel, FundamentalMomentumWeight = req.Weights.FundamentalMomentum,
                BuyThreshold = req.BuyThreshold,
                WatchThreshold = current.WatchThreshold,
                StopLossPctDefault = current.StopLossPctDefault,
                Source = "StrategyLab",
            };

            // A Lab apply makes any pending auto-refinement suggestion stale -
            // it was computed against the weights being replaced.
            await suggestionRepo.SupersedeAllPendingAsync(ctx.AccountId, account.TradingMode);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var suggestion = await suggestionRepo.AddAsync(new RefinementSuggestion
            {
                AccountId = ctx.AccountId,
                TradingMode = account.TradingMode,
                Origin = RefinementOrigin.StrategyLab,
                GeneratedAt = DateTime.UtcNow,
                AnalysisPeriodStart = today,
                AnalysisPeriodEnd = today,
                TradeCountAnalysed = req.TradeCount,
                OverallWinRate = Math.Round(req.WinRate, 4),
                CurrentWeightsJson = JsonSerializer.Serialize(current),
                SuggestedWeightsJson = JsonSerializer.Serialize(suggested),
                ComponentAnalysisJson = "[]",
                AssessmentSummary = req.EvidenceSummary,
                ConfidenceLevel = req.Confidence,
                Status = RefinementStatus.Pending, // consumed by ApplyAsync below
                IsShadowMode = false,              // explicit user action, never shadow-gated
            });

            var result = await applyService.ApplyAsync(ctx.AccountId, suggestion.Id, ct: ct);
            return result.Success
                ? Results.Ok(new { success = true, suggestionId = suggestion.Id, weightsId = result.NewWeightsId })
                : Results.BadRequest(new { success = false, message = result.Error });
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
        IStrategyWeightsRepository weightsRepo, IAccountRiskProfileRepository riskProfileRepo, int accountId, CancellationToken ct)
    {
        var prod = await weightsRepo.GetActiveWeightsAsync(accountId);
        if (prod is null) return null;
        var profile = await riskProfileRepo.GetAsync(accountId, ct);
        return new HistoricBacktestCandidate(
            "Production baseline",
            new HistoricBacktestWeights(
                prod.RsiWeight, prod.MacdWeight, prod.VolumeWeight, prod.SentimentWeight,
                prod.SetupQualityWeight, prod.RelativeStrengthWeight, prod.PriceLevelWeight, prod.FundamentalMomentumWeight),
            prod.BuyThreshold,
            // Production policy: Breakout setups are hard-capped at Watch in
            // ResearchPipeline, so the baseline replays with them excluded.
            ExcludeBreakout: true,
            AutopauseDuringBear: profile.AutopauseDuringBear);
    }
}
