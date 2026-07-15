using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Functions;

// Runs Strategy Lab historic-market simulations: loads the shared
// HistoricalCandles dataset once, then depending on the request mode runs a
// single config, an A/B pair (user dials vs the production baseline snapshot),
// or an optimizer sweep (candidates evaluated on a train window, winner
// validated out-of-sample). Stores the result on the BacktestRun row the UI
// polls. Deliberately does NOT rethrow on simulation failure - the error lands
// on the run row for the user; redelivering a broken request would just fail
// again.
public class BacktestConsumerFunction(
    IBacktestRunRepository runs,
    IHistoricalCandleRepository candles,
    IAccountRiskProfileRepository riskProfileRepo,
    IUserHttpClientFactory clientFactory,
    IOptions<ClaudeConfig> claudeConfig,
    SwingTrader.Infrastructure.Market.IMarketUniverseService universe,
    ILogger<BacktestConsumerFunction> logger)
{
    // Must match the API's camelCase JSON output - this string gets embedded
    // verbatim into the poll response, so it can't be re-cased downstream.
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    [Function("BacktestConsumer")]
    public async Task Run(
        [ServiceBusTrigger("backtest-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<BacktestJobMessage>(messageBody)!;
        var run = await runs.GetByIdAsync(message.AccountId, message.BacktestRunId);
        if (run is null)
        {
            logger.LogWarning("Backtest run {RunId} not found for account {AccountId} — dropping", message.BacktestRunId, message.AccountId);
            return;
        }
        if (run.Status is "Completed" or "Failed") return; // redelivery of a finished run

        run.Status = "Running";
        run.StartedAt = DateTime.UtcNow;
        await runs.UpdateAsync(run);

        try
        {
            var request = JsonSerializer.Deserialize<HistoricBacktestRequest>(run.RequestJson)
                ?? throw new InvalidOperationException("Unreadable backtest request.");

            var bySymbol = await candles.GetAllBySymbolAsync(ct);
            if (bySymbol.Count == 0)
                throw new InvalidOperationException("No historic market data synced yet — run a candle sync first.");

            var bars = bySymbol.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(c => new DailyBar(
                    c.Date.ToDateTime(TimeOnly.MinValue), c.Open, c.High, c.Low, c.Close, c.Volume)).ToArray(),
                StringComparer.OrdinalIgnoreCase);

            // The engine mirrors the account's baseline (Neutral) risk book so
            // the Lab tests a reproducible strategy rather than one that shifts
            // with today's live regime. (Per-regime backtesting is Phase 2.)
            var profile = await riskProfileRepo.GetAsync(message.AccountId, MarketRegime.Neutral, ct);

            // GICS-driven sector-ETF benchmarks for the RS component - the
            // SAME mapping live research uses. A universe outage degrades to
            // the engine's built-in override-or-SPY fallback, never fails the
            // run.
            IReadOnlyDictionary<string, string>? sectorEtfs = null;
            try
            {
                sectorEtfs = await universe.GetSectorEtfMapAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sector-ETF map unavailable — backtest RS falls back to the legacy map");
            }

            run.ResultJson = request.Mode switch
            {
                "ab" => await RunAbAsync(request, bars, profile, sectorEtfs, ct),
                "sweep" => await RunSweepAsync(message.AccountId, run, request, bars, profile, sectorEtfs, ct),
                "validate" => await RunValidateAsync(request, bars, profile, sectorEtfs, ct),
                "montecarlo" => await RunMonteCarloAsync(request, bars, profile, sectorEtfs, ct),
                _ => await RunSingleAsync(request, bars, profile, sectorEtfs, ct),
            };
            run.Status = "Completed";
            run.CompletedAt = DateTime.UtcNow;
            await runs.UpdateAsync(run);
            logger.LogInformation("Backtest run {RunId} ({Mode}) completed", run.Id, request.Mode ?? "single");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest run {RunId} failed", run.Id);
            run.Status = "Failed";
            run.Error = ex.Message;
            run.CompletedAt = DateTime.UtcNow;
            await runs.UpdateAsync(run);
        }
    }

    private static HistoricConfig ToConfig(
        HistoricBacktestWeights w, decimal buyThreshold, bool excludeBreakout, bool autopauseDuringBear,
        AccountRiskProfile profile, HistoricTradingRules? rules = null) =>
        new(new StrategyWeights
        {
            RsiWeight = w.Rsi, MacdWeight = w.Macd, VolumeWeight = w.Volume, SentimentWeight = w.Sentiment,
            SetupQualityWeight = w.SetupQuality, RelativeStrengthWeight = w.RelativeStrength,
            PriceLevelWeight = w.PriceLevel, FundamentalMomentumWeight = w.FundamentalMomentum,
        }, buyThreshold, excludeBreakout,
        // SPY-below-200dma entry pause approximates the live bear autopause.
        // Per-request/candidate (a Lab dial), not the profile setting - the
        // baseline candidate snapshots the profile value at queue time.
        RegimeFilter: autopauseDuringBear,
        // Trading rules: explicit Lab overrides win; otherwise the account's
        // live risk profile - including the trailing shape, which previously
        // sat as hardcoded 5%/3% constants in the engine and silently
        // diverged from live whenever the profile differed.
        MaxOpenPositions: rules?.MaxOpenPositions ?? profile.MaxOpenPositions,
        MaxHoldDays: rules?.MaxHoldDays ?? profile.MaxHoldDays,
        ExcludedSetups: ParseSetups(rules?.ExcludedSetups),
        TrailingActivationPct: rules?.TrailingActivationPct ?? (decimal)profile.TrailingActivationPct,
        TrailingDistancePct: rules?.TrailingDistancePct ?? (decimal)profile.TrailingDistancePct,
        // Null rule = the live risk-profile setting (the tables are gone), so
        // an untouched Lab run simulates exactly what production will do.
        StopLossPct: rules?.StopLossPct ?? profile.StopLossPct,
        TargetPct: rules?.TargetPct ?? profile.TargetPct,
        SimulateProbation: rules?.SimulateProbation ?? true,
        MinHoldDays: rules?.MinHoldDays ?? profile.MinHoldDays,
        MomentumHealthThreshold: rules?.MomentumHealthThreshold ?? profile.MomentumHealthThreshold,
        PositionFraction: rules?.PositionFraction ?? 0.10m,
        // Null keeps the legacy flat sizing (the long-standing engine
        // behaviour); setting it switches to the live tier-pool model.
        ActiveCapitalPct: rules?.ActiveCapitalPct,
        MaxPositionPctOfActive: rules?.MaxPositionPctOfActive ?? profile.MaxPositionPctOfActive);

    // Unknown names are ignored rather than failing the run - the list comes
    // from the UI, but the request JSON is stored and could be replayed after
    // an enum rename.
    private static IReadOnlyCollection<SwingTrader.Core.Enums.SetupType>? ParseSetups(List<string>? names) =>
        names is null
            ? null
            : names.Select(n => Enum.TryParse<SwingTrader.Core.Enums.SetupType>(n, ignoreCase: true, out var s) ? s : (SwingTrader.Core.Enums.SetupType?)null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToList();

    private static async Task<string> RunSingleAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var result = await HistoricBacktester.RunAsync(
            bars, ToConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout, request.AutopauseDuringBear, profile, request.Rules), sectorEtfs, ct);
        // Trade log stays out of the stored JSON - it can be thousands of
        // rows; the headline stats + buckets are what the UI shows.
        return JsonSerializer.Serialize(result with { TradeLog = [] }, CamelCase);
    }

    // A/B: both configs over the identical full window. Candidates carry their
    // labels ("Your dials" / "Production baseline") from queue time.
    private static async Task<string> RunAbAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var candidates = request.Candidates
            ?? throw new InvalidOperationException("A/B request carries no candidates.");

        var results = new List<object>();
        foreach (var c in candidates)
        {
            var r = await HistoricBacktester.RunAsync(bars, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, c.Rules), sectorEtfs, ct);
            results.Add(new
            {
                label = c.Label,
                weights = c.Weights,
                buyThreshold = c.BuyThreshold,
                excludeBreakout = c.ExcludeBreakout,
                autopauseDuringBear = c.AutopauseDuringBear,
                result = r with { TradeLog = [] },
            });
        }
        return JsonSerializer.Serialize(new { mode = "ab", candidates = results }, CamelCase);
    }

    // Sweep: candidates generated around the baseline, evaluated on the train
    // window (earlier ~70%), best eligible one validated on the held-out
    // remainder it never saw. Claude explanation is best-effort - a missing
    // writeup never fails the sweep.
    private async Task<string> RunSweepAsync(
        int accountId, BacktestRun run, HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var baseline = request.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("Sweep request carries no baseline candidate.");

        // The production rule values, so rule-search grid points that equal
        // production are skipped (they'd just duplicate the baseline run).
        var productionRules = new HistoricTradingRules(
            MaxHoldDays: profile.MaxHoldDays,
            MaxOpenPositions: profile.MaxOpenPositions,
            TrailingActivationPct: (decimal)profile.TrailingActivationPct,
            TrailingDistancePct: (decimal)profile.TrailingDistancePct,
            StopLossPct: profile.StopLossPct,
            TargetPct: profile.TargetPct,
            MinHoldDays: profile.MinHoldDays,
            MomentumHealthThreshold: profile.MomentumHealthThreshold);
        var candidates = SweepOptimizer.GenerateCandidates(baseline, request.SearchRules, productionRules);

        // Progress the UI polls: total covers BOTH search pools (traditional
        // sweep + ML search) up front, completed ticks up per candidate below
        // so a determinate progress bar can render instead of a static
        // "expect N minutes" spinner.
        run.TotalCandidates = candidates.Count + MlSweepOptimizer.ActualCandidateCount;
        run.CompletedCandidates = 0;
        await runs.UpdateAsync(run);

        var (train, holdout) = SweepOptimizer.SplitBars(bars, HistoricBacktester.WarmupBars);
        var trainSpy = train["SPY"];
        var holdoutSpy = holdout["SPY"];

        // Baseline first - its drawdown sets the ceiling for everyone else.
        var baselineTrain = await HistoricBacktester.RunAsync(
            train, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear, profile, baseline.Rules), sectorEtfs, ct);
        var baselineSummary = SweepOptimizer.Summarise(candidates[0], baselineTrain, trainSpy, baselineTrain.MaxDrawdownPct);
        run.CompletedCandidates = 1;
        await runs.UpdateAsync(run);

        var summaries = new List<SweepCandidateResult> { baselineSummary };
        var trainResults = new Dictionary<string, HistoricResult> { [baselineSummary.Label] = baselineTrain };
        foreach (var c in candidates.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            // c.Rules rides into the engine config - null for weight variants
            // (production rules), set for the rule-search candidates.
            var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, c.Rules), sectorEtfs, ct);
            summaries.Add(SweepOptimizer.Summarise(c, r, trainSpy, baselineTrain.MaxDrawdownPct, baselineTrain.Trades));
            trainResults[c.Label] = r;
            logger.LogInformation("Sweep candidate '{Label}': {Trades} trades, {Adj}% adjusted expectancy", c.Label, r.Trades, summaries[^1].AdjustedExpectancyPct);

            run.CompletedCandidates++;
            await runs.UpdateAsync(run);
        }

        // Everyone is ranked on RobustScorePct (worse train-window half,
        // LCB-discounted), NOT the raw AdjustedExpectancyPct - so a big mean
        // from a small or one-regime sample can't win on luck at any of the
        // three selection points (best-of-pool x2, and the overall winner).
        var bestTraditional = summaries
            .Where(s => s.MetConstraints)
            .OrderByDescending(s => s.RobustScorePct)
            .FirstOrDefault();

        // Second search pool: the same dial space and guardrails, covered by
        // successive-halving CMA-ES instead of nudges + a random fill - see
        // MlSweepOptimizer for why that's a denser per-evaluation search of
        // the same simplex. Offspring within a generation evaluate in
        // parallel (the engine is stateless over the read-only bars), so the
        // one piece of shared state - the progress row - is serialized here;
        // labels aren't assigned until the optimizer reassembles results in
        // deterministic order, so nothing else in this delegate needs the
        // candidate's identity.
        var progressGate = new SemaphoreSlim(1, 1);
        var mlEvaluations = await MlSweepOptimizer.OptimizeAsync(
            baseline,
            async (c, token) =>
            {
                var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile), sectorEtfs, token);
                await progressGate.WaitAsync(token);
                try
                {
                    run.CompletedCandidates++;
                    await runs.UpdateAsync(run);
                }
                finally
                {
                    progressGate.Release();
                }
                return r;
            },
            trainSpy, baselineTrain.MaxDrawdownPct, baselineTrain.Trades, ct,
            maxParallelism: Math.Clamp(Environment.ProcessorCount, 1, 4));

        foreach (var e in mlEvaluations)
        {
            summaries.Add(e.Summary);
            trainResults[e.Summary.Label] = e.Result;
        }
        logger.LogInformation("ML search: {Count} candidates evaluated via successive-halving CMA-ES", mlEvaluations.Count);

        var bestMlSearch = mlEvaluations
            .Select(e => e.Summary)
            .Where(s => s.MetConstraints)
            .OrderByDescending(s => s.RobustScorePct)
            .FirstOrDefault();

        var winnerSummary = summaries
            .Where(s => s.MetConstraints)
            .OrderByDescending(s => s.RobustScorePct)
            .FirstOrDefault()
            ?? baselineSummary; // nothing eligible - baseline "wins" by default

        var winnerSource = winnerSummary.Label == baselineSummary.Label
            ? "Baseline (no candidate improved on it)"
            : bestMlSearch is not null && winnerSummary.Label == bestMlSearch.Label
                ? "ML search (CMA-ES)"
                : "Traditional sweep";

        // Out-of-sample validation: winner and baseline on the held-out window.
        var winnerHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(winnerSummary.Weights, winnerSummary.BuyThreshold, winnerSummary.ExcludeBreakout, winnerSummary.AutopauseDuringBear, profile, winnerSummary.Rules), sectorEtfs, ct);
        var baselineHoldout = winnerSummary.Label == baselineSummary.Label
            ? winnerHoldout
            : await HistoricBacktester.RunAsync(
                holdout, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear, profile, baseline.Rules), sectorEtfs, ct);

        var validation = SweepOptimizer.BuildValidation(
            trainResults[winnerSummary.Label], winnerHoldout, baselineHoldout, trainSpy, holdoutSpy);

        var explanation = await TryExplainAsync(accountId, baselineSummary, winnerSummary, validation, summaries, ct);

        var sweep = new SweepResult(
            "sweep", baselineSummary, winnerSummary, validation, summaries, explanation,
            bestTraditional, bestMlSearch, winnerSource);
        return JsonSerializer.Serialize(sweep, CamelCase);
    }

    // Out-of-sample validation of ONE hand-tuned configuration: the sweep's
    // train/holdout split and hold-up verdict, applied on demand. Candidates:
    // [0] = the user's dials+rules, [1] = the production baseline snapshot
    // (needed because "held up" includes beating the baseline out-of-sample).
    private static async Task<string> RunValidateAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var candidates = request.Candidates is { Count: 2 }
            ? request.Candidates
            : throw new InvalidOperationException("Validate request needs exactly [user, baseline] candidates.");
        var user = candidates[0];
        var baseline = candidates[1];

        var (train, holdout) = SweepOptimizer.SplitBars(bars, HistoricBacktester.WarmupBars);
        var trainSpy = train["SPY"];
        var holdoutSpy = holdout["SPY"];

        var userTrain = await HistoricBacktester.RunAsync(
            train, ToConfig(user.Weights, user.BuyThreshold, user.ExcludeBreakout, user.AutopauseDuringBear, profile, user.Rules), sectorEtfs, ct);
        var userHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(user.Weights, user.BuyThreshold, user.ExcludeBreakout, user.AutopauseDuringBear, profile, user.Rules), sectorEtfs, ct);
        var baselineHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear, profile, baseline.Rules), sectorEtfs, ct);

        var validation = SweepOptimizer.BuildValidation(userTrain, userHoldout, baselineHoldout, trainSpy, holdoutSpy);
        return JsonSerializer.Serialize(new ValidateResult("validate", validation), CamelCase);
    }

    // Monte Carlo: one full-window run of the config, then bootstrap-resample
    // its trade log to measure how much of the result is trade ORDER luck
    // (sequence risk) versus trade quality - the complement to the
    // train/holdout validate, which measures window (period) luck.
    private static async Task<string> RunMonteCarloAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var cfg = ToConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout,
            request.AutopauseDuringBear, profile, request.Rules);
        var result = await HistoricBacktester.RunAsync(bars, cfg, sectorEtfs, ct);

        // The equity slice each resampled trade compounds against: flat-mode
        // fraction directly, or the pool mode's effective per-position share.
        var fraction = cfg.ActiveCapitalPct is { } pool
            ? pool * cfg.MaxPositionPctOfActive
            : cfg.PositionFraction;

        var mc = MonteCarloSimulator.Run(result, fraction);
        return JsonSerializer.Serialize(mc, CamelCase);
    }

    private async Task<string?> TryExplainAsync(
        int accountId, SweepCandidateResult baseline, SweepCandidateResult winner,
        SweepValidation validation, List<SweepCandidateResult> candidates, CancellationToken ct)
    {
        try
        {
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(accountId, ct);
            var cfg = claudeConfig.Value;
            var response = await claude.SendMessageAsync(new ClaudeRequest(
                cfg.RefinementModel ?? cfg.PremiumModel, cfg.MaxTokens,
                LabAnalysisPrompts.SystemPrompt,
                [new ClaudeMessage("user", LabAnalysisPrompts.BuildSweepExplanationPrompt(baseline, winner, validation, candidates))]));
            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var (analysis, _) = LabAnalysisPrompts.ParseResponse(raw);
            return analysis;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sweep explanation failed for account {AccountId} — result ships without a writeup", accountId);
            return null;
        }
    }
}
