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
            ISetupTacticsRepository setupTacticsRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var sum = req.Weights.Rsi + req.Weights.Macd + req.Weights.Volume
                + req.Weights.SetupQuality + req.Weights.RelativeStrength + req.Weights.PriceLevel;
            if (Math.Abs(sum - 1.0m) > 0.01m)
                return Results.BadRequest(new { message = $"Weights must sum to 1.0 (currently {sum:F2})." });

            if (req.DataSource.Equals("historic", StringComparison.OrdinalIgnoreCase))
            {
                // Historic runs take minutes (full engine over ~1M bars) - they
                // execute as a Service Bus job; the UI polls the run row.
                if (serviceBus is null)
                    return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

                var userWeights = new HistoricBacktestWeights(
                    req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume,
                    req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel);

                HistoricBacktestRequest historicRequest;
                if (req.CompareBaseline)
                {
                    // A/B: snapshot the production dials into the request NOW,
                    // so the comparison is labelled with what was actually
                    // evaluated even if production changes mid-run. The
                    // baseline runs with the ACCOUNT's autopause setting; the
                    // user's column runs with the checkbox value.
                    var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct, setupTacticsRepo);
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
                        ],
                        // Regime frame for both columns; the per-regime autopause
                        // overrides land on the user column only (consumer side).
                        RegimeMode: req.RegimeMode,
                        RegimeOverrides: req.RegimeOverrides);
                }
                else
                {
                    historicRequest = new HistoricBacktestRequest(
                        userWeights, req.BuyThreshold, req.ExcludeBreakout,
                        AutopauseDuringBear: req.AutopauseDuringBear,
                        Rules: req.Rules,
                        RegimeMode: req.RegimeMode,
                        RegimeOverrides: req.RegimeOverrides);
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
            OptimizeRequest? req,
            IBacktestRunRepository runs,
            IStrategyWeightsRepository weightsRepo,
            IAccountRiskProfileRepository riskProfileRepo,
            ISetupTacticsRepository setupTacticsRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct, setupTacticsRepo);
            if (baseline is null)
                return Results.BadRequest(new { message = "No active production weights found to optimize around." });

            var run = await runs.AddAsync(new BacktestRun
            {
                AccountId = ctx.AccountId,
                RequestJson = JsonSerializer.Serialize(new HistoricBacktestRequest(
                    baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                    Mode: "sweep",
                    Candidates: [baseline],
                    SearchRules: req?.SearchRules ?? false)),
            });

            await using var sender = serviceBus.CreateSender("backtest-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new BacktestJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), run.Id))), ct);

            return Results.Ok(new { backtestRunId = run.Id });
        });

        // Setup-contribution (leave-one-out ablation): re-runs the production
        // strategy excluding each setup in turn, on the train + held-out windows,
        // to show each setup's MARGINAL out-of-sample effect - which pay their way
        // and which are a drag. Queued like any historic run and polled via the
        // shared status endpoint.
        api.MapPost("/strategy-lab/setup-contribution", async (
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
                return Results.BadRequest(new { message = "No active production weights found to analyse." });

            var run = await runs.AddAsync(new BacktestRun
            {
                AccountId = ctx.AccountId,
                RequestJson = JsonSerializer.Serialize(new HistoricBacktestRequest(
                    baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                    Mode: "ablation",
                    Candidates: [baseline])),
            });

            await using var sender = serviceBus.CreateSender("backtest-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new BacktestJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), run.Id))), ct);

            return Results.Ok(new { backtestRunId = run.Id });
        });

        // Regime comparison: runs the live production strategy over the full
        // period under each regime book forced throughout, plus a Mixed run that
        // switches book by the regime detected at each simulated day. Shows
        // whether a regime mix beats a single master ruleset. The baseline
        // honours the per-setup live switch so it reflects the book traded.
        api.MapPost("/strategy-lab/regime-compare", async (
            IBacktestRunRepository runs,
            IStrategyWeightsRepository weightsRepo,
            IAccountRiskProfileRepository riskProfileRepo,
            ISetupTacticsRepository setupTacticsRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct, setupTacticsRepo);
            if (baseline is null)
                return Results.BadRequest(new { message = "No active production weights found to compare." });

            var run = await runs.AddAsync(new BacktestRun
            {
                AccountId = ctx.AccountId,
                RequestJson = JsonSerializer.Serialize(new HistoricBacktestRequest(
                    baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                    Mode: "regime",
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
            ISetupTacticsRepository setupTacticsRepo,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var userWeights = new HistoricBacktestWeights(
                req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume,
                req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel);
            var baseline = await SnapshotBaselineAsync(weightsRepo, riskProfileRepo, ctx.AccountId, ct, setupTacticsRepo);
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
                req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume,
                req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel);

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

        // Latest run of a given mode - an in-flight run preferred over the
        // newest completed one, so on tab load the UI can either restore the
        // most recent optimizer result or REATTACH its poll to a sweep still
        // running server-side (both were lost on page refresh before; the
        // results were always persisted, only the run id lived in component
        // memory).
        api.MapGet("/strategy-lab/backtest/latest", async (
            string mode, IBacktestRunRepository runs, IAccountContext ctx, CancellationToken ct) =>
        {
            var run = await runs.GetLatestByModeAsync(ctx.AccountId, mode, ct);
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

            var sum = req.Weights.Rsi + req.Weights.Macd + req.Weights.Volume
                + req.Weights.SetupQuality + req.Weights.RelativeStrength + req.Weights.PriceLevel;
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
                VolumeWeight = req.Weights.Volume,
                SetupQualityWeight = req.Weights.SetupQuality, RelativeStrengthWeight = req.Weights.RelativeStrength,
                PriceLevelWeight = req.Weights.PriceLevel,
                ForwardSentimentWeight = current.ForwardSentimentWeight,
                ForwardFundamentalWeight = current.ForwardFundamentalWeight,
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

        // Strategy Lab history tabs (Optimizer History / A/B History): completed
        // runs of a mode, newest first, each carrying its applyable config +
        // headline stats + (A/B only) the risk-rule overrides it tested.
        api.MapGet("/strategy-lab/history", async (
            string mode, int? limit, IBacktestRunRepository runs, IAccountContext ctx, CancellationToken ct) =>
        {
            if (mode is not ("ab" or "sweep"))
                return Results.BadRequest(new { message = "mode must be 'ab' or 'sweep'." });

            var list = await runs.GetCompletedByModeAsync(ctx.AccountId, mode, Math.Clamp(limit ?? 20, 1, 100), ct);
            var items = list.Select(r =>
            {
                var cfg = Agents.Backtesting.BacktestApplyExtractor.Extract(mode, r.RequestJson, r.ResultJson);
                return new
                {
                    r.Id,
                    mode,
                    r.CompletedAt,
                    canApply = cfg is not null,
                    label = cfg?.Label,
                    weights = cfg?.Weights,
                    buyThreshold = cfg?.BuyThreshold,
                    rules = cfg?.Rules,
                    // A run has risk overrides to apply if it changed the exit/
                    // tactic rules OR flipped the bear-autopause vs its baseline.
                    hasRiskOverrides = cfg is not null && (cfg.Rules is not null || cfg.AutopauseChanged),
                    stats = cfg is null ? null : new
                    {
                        cfg.Stats.Trades,
                        cfg.Stats.WinRatePct,
                        cfg.Stats.TotalReturnPct,
                        cfg.Stats.MaxDrawdownPct,
                        cfg.Stats.ProfitFactor,
                        cfg.Stats.ExpectancyPct,
                    },
                };
            }).ToList();
            return Results.Ok(items);
        });

        // Apply a historic run's config to live settings. Owner-only. Weights
        // ride the same audited path as the Lab's live apply (a StrategyLab
        // RefinementSuggestion, then ApplyRefinementService); risk settings map
        // the run's non-null rule overrides onto the live risk profile. At
        // least one of the two must be requested.
        api.MapPost("/strategy-lab/backtest/{runId:int}/apply", async (
            int runId,
            BacktestApplyRequest req,
            IBacktestRunRepository runs,
            IStrategyWeightsRepository weightsRepo,
            IRefinementSuggestionRepository suggestionRepo,
            IApplyRefinementService applyService,
            IAccountRiskProfileRepository riskRepo,
            ISetupTacticsRepository setupTacticsRepo,
            IAccountRepository accounts,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            if (!req.ApplyWeights && !req.ApplyRiskSettings)
                return Results.BadRequest(new { message = "Select weights, risk settings, or both to apply." });

            var run = await runs.GetByIdAsync(ctx.AccountId, runId);
            if (run is null) return Results.NotFound();

            var mode = run.RequestJson.Contains("\"Mode\":\"ab\"") ? "ab"
                : run.RequestJson.Contains("\"Mode\":\"sweep\"") ? "sweep" : null;
            var cfg = Agents.Backtesting.BacktestApplyExtractor.Extract(mode, run.RequestJson, run.ResultJson);
            if (cfg is null) return Results.BadRequest(new { message = "This run has no applyable configuration." });
            if (req.ApplyRiskSettings && cfg.Rules is null && !cfg.AutopauseChanged)
                return Results.BadRequest(new { message = "This run has no risk-setting overrides to apply." });

            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            int? weightsId = null;
            if (req.ApplyWeights)
            {
                var current = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
                if (current is null) return Results.BadRequest(new { message = "No active production weights found." });

                var suggested = new StrategyWeights
                {
                    RsiWeight = cfg.Weights.Rsi, MacdWeight = cfg.Weights.Macd, VolumeWeight = cfg.Weights.Volume,
                    SetupQualityWeight = cfg.Weights.SetupQuality,
                    RelativeStrengthWeight = cfg.Weights.RelativeStrength, PriceLevelWeight = cfg.Weights.PriceLevel,
                    // Forward blend isn't tuned in the Lab - carry live values forward.
                    ForwardSentimentWeight = current.ForwardSentimentWeight,
                    ForwardFundamentalWeight = current.ForwardFundamentalWeight,
                    BuyThreshold = cfg.BuyThreshold, WatchThreshold = current.WatchThreshold,
                    StopLossPctDefault = current.StopLossPctDefault, Source = "StrategyLab",
                };
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
                    TradeCountAnalysed = cfg.Stats.Trades,
                    OverallWinRate = Math.Round(cfg.Stats.WinRatePct, 4),
                    CurrentWeightsJson = JsonSerializer.Serialize(current),
                    SuggestedWeightsJson = JsonSerializer.Serialize(suggested),
                    ComponentAnalysisJson = "[]",
                    AssessmentSummary =
                        $"Applied from {(mode == "ab" ? "A/B" : "optimizer")} run #{run.Id} ('{cfg.Label}'): " +
                        $"{cfg.Stats.Trades} trades, {cfg.Stats.WinRatePct * 100:0.#}% win rate, {cfg.Stats.TotalReturnPct:0.#}% total return.",
                    ConfidenceLevel = RefinementConfidenceLevel.Medium,
                    Status = RefinementStatus.Pending,
                    IsShadowMode = false,
                });
                var applyResult = await applyService.ApplyAsync(ctx.AccountId, suggestion.Id, ct: ct);
                if (!applyResult.Success) return Results.BadRequest(new { success = false, message = applyResult.Error });
                weightsId = applyResult.NewWeightsId;
            }

            var appliedRisk = false;
            if (req.ApplyRiskSettings && cfg.Rules is not null)
            {
                // Profile-level rules (max positions, probation day, health
                // floor) + the fallback stop/target/hold seed.
                var profile = await riskRepo.GetAsync(ctx.AccountId, ct);
                Agents.Backtesting.BacktestRiskRuleMapper.Apply(profile, cfg.Rules);
                await riskRepo.UpdateAsync(profile, ct);

                // Tactic winners (uniform or per-setup stop/target/guide-hold/
                // trailing) must land on the SetupTactics rows - that's what
                // live execution actually reads. Only rows that moved are saved;
                // UpdateAsync validates each.
                var tacticRows = await setupTacticsRepo.GetAllAsync(ctx.AccountId, ct);
                var changedTactics = Agents.Backtesting.SetupTacticsRuleMapper.Apply(tacticRows, cfg.Rules);
                foreach (var row in changedTactics)
                    await setupTacticsRepo.UpdateAsync(row, ct);

                appliedRisk = true;
            }

            // A bear-autopause winner lands on the Bear regime book (where the
            // live "pause new entries in a bear" decision lives). Independent of
            // the rule/tactic apply above, so an autopause-only winner applies too.
            if (req.ApplyRiskSettings && cfg.AutopauseChanged)
            {
                var bearBook = await riskRepo.GetAsync(ctx.AccountId, MarketRegime.Bear, ct);
                bearBook.AutopauseTrading = cfg.AutopauseDuringBear;
                await riskRepo.UpdateAsync(bearBook, ct);
                appliedRisk = true;
            }

            return Results.Ok(new { success = true, weightsId, appliedWeights = req.ApplyWeights, appliedRisk });
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
        IStrategyWeightsRepository weightsRepo, IAccountRiskProfileRepository riskProfileRepo, int accountId, CancellationToken ct,
        ISetupTacticsRepository? setupTacticsRepo = null)
    {
        var prod = await weightsRepo.GetActiveWeightsAsync(accountId);
        if (prod is null) return null;
        // Backtest autopause-during-bear mirrors the Bear regime book's toggle
        // (the live "pause entries in a bear" decision now lives per book).
        var bearBook = await riskProfileRepo.GetAsync(accountId, MarketRegime.Bear, ct);
        // Production honours the per-setup live switch: setups turned OFF for live
        // trading are excluded from the baseline so "vs production" reflects the
        // book actually being traded. Passed only where the baseline means
        // "current live" (A/B, optimizer, validate) - the ablation analysis omits
        // it so it can still measure every setup's marginal effect.
        HistoricTradingRules? rules = null;
        if (setupTacticsRepo is not null)
        {
            var disabled = await setupTacticsRepo.GetDisabledSetupsAsync(accountId, ct);
            if (disabled.Count > 0)
                rules = new HistoricTradingRules(ExcludedSetups: disabled.Select(s => s.ToString()).ToList());
        }
        return new HistoricBacktestCandidate(
            "Production baseline",
            new HistoricBacktestWeights(
                prod.RsiWeight, prod.MacdWeight, prod.VolumeWeight,
                prod.SetupQualityWeight, prod.RelativeStrengthWeight, prod.PriceLevelWeight),
            prod.BuyThreshold,
            // Live no longer excludes Breakout setups (docs/setup-tactics-plan),
            // so the production baseline replays with them included.
            ExcludeBreakout: false,
            AutopauseDuringBear: bearBook.AutopauseTrading,
            Rules: rules);
    }
}
