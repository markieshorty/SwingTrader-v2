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
    ISetupTacticsRepository setupTacticsRepo,
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

            // The account's live per-setup tactics, loaded ONCE (no per-candidate
            // DB reads - the Basic-tier DB only sees this and the candle load per
            // job). Seeds every candidate's baseline so an untouched run mirrors
            // live; the Lab's tactics editor overlays overrides on top per-setup.
            var accountTactics = (await setupTacticsRepo.GetAllAsync(message.AccountId, ct))
                .ToDictionary(
                    t => t.SetupType,
                    t => new HistoricSetupTactics(
                        t.StopLossPct, t.TargetPct, t.GuideHoldDays,
                        (decimal)t.TrailingActivationPct, (decimal)t.TrailingDistancePct));

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
                "ab" => await RunAbAsync(request, bars, profile, message.AccountId, accountTactics, sectorEtfs, ct),
                "sweep" => await RunSweepAsync(message.AccountId, run, request, bars, profile, accountTactics, sectorEtfs, ct),
                "validate" => await RunValidateAsync(request, bars, profile, accountTactics, sectorEtfs, ct),
                "montecarlo" => await RunMonteCarloAsync(request, bars, profile, accountTactics, sectorEtfs, ct),
                "ablation" => await RunSetupAblationAsync(run, request, bars, profile, accountTactics, sectorEtfs, ct),
                "regime" => await RunRegimeComparisonAsync(run, request, bars, message.AccountId, accountTactics, sectorEtfs, ct),
                _ => await RunSingleAsync(request, bars, profile, message.AccountId, accountTactics, sectorEtfs, ct),
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
        AccountRiskProfile profile, IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        HistoricTradingRules? rules = null) =>
        new(new StrategyWeights
        {
            RsiWeight = w.Rsi, MacdWeight = w.Macd, VolumeWeight = w.Volume,
            SetupQualityWeight = w.SetupQuality, RelativeStrengthWeight = w.RelativeStrength,
            PriceLevelWeight = w.PriceLevel,
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
        // Flat sizing mirrors live: each position is FlatPositionPct of equity.
        PositionFraction: rules?.PositionFraction ?? profile.FlatPositionPct,
        // Lab-only pool-sizing sim (no live equivalent since the tier ladder
        // was removed): null keeps flat sizing; the two dials below only apply
        // when a Lab run explicitly sets ActiveCapitalPct.
        ActiveCapitalPct: rules?.ActiveCapitalPct,
        MaxPositionPctOfActive: rules?.MaxPositionPctOfActive ?? 0.33m,
        // Per-setup tactics: the account's live set, with any Lab overrides
        // overlaid. Built once per candidate from data already in memory - no
        // DB touch.
        SetupTactics: MergeTactics(accountTactics, rules));

    // Builds the per-setup tactics map applied to a candidate, layering two
    // kinds of override onto the account's live baseline:
    //   1. UNIFORM rule overrides (rules.StopLossPct/TargetPct/MaxHoldDays/
    //      Trailing*) - a single value the optimizer's rule-search or the Lab's
    //      global fields set. Applied to EVERY setup, so "Stop 5%" means 5% on
    //      all of them. Without this, per-setup tactics would silently swallow
    //      those candidates and the rule search would be inert.
    //   2. PER-SETUP overrides (rules.SetupTactics) - the Lab's tactics editor
    //      / per-setup optimizer search, a full replace for each named setup.
    // Null rules (an untouched baseline run) leaves the account map as-is, so
    // the run mirrors live exactly.
    private static IReadOnlyDictionary<SetupType, HistoricSetupTactics> MergeTactics(
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> baseline,
        HistoricTradingRules? rules)
    {
        if (rules is null) return baseline;
        var merged = new Dictionary<SetupType, HistoricSetupTactics>(baseline);

        // 1. Uniform rule fields (only the ones this candidate actually set).
        if (rules.StopLossPct is not null || rules.TargetPct is not null || rules.MaxHoldDays is not null
            || rules.TrailingActivationPct is not null || rules.TrailingDistancePct is not null)
        {
            foreach (var setup in merged.Keys.ToList())
            {
                var b = merged[setup];
                merged[setup] = b with
                {
                    StopLossPct = rules.StopLossPct ?? b.StopLossPct,
                    TargetPct = rules.TargetPct ?? b.TargetPct,
                    GuideHoldDays = rules.MaxHoldDays ?? b.GuideHoldDays,
                    TrailingActivationPct = rules.TrailingActivationPct ?? b.TrailingActivationPct,
                    TrailingDistancePct = rules.TrailingDistancePct ?? b.TrailingDistancePct,
                };
            }
        }

        // 2. Per-setup overrides win over the uniform layer.
        foreach (var o in rules.SetupTactics ?? [])
        {
            if (!Enum.TryParse<SetupType>(o.Setup, ignoreCase: true, out var setup)) continue;
            merged[setup] = new HistoricSetupTactics(
                o.StopLossPct, o.TargetPct, o.GuideHoldDays,
                o.TrailingActivationPct, o.TrailingDistancePct);
        }
        return merged;
    }

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

    private async Task<string> RunSingleAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile, int accountId,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        HistoricConfig cfg;
        if (!string.IsNullOrEmpty(request.RegimeMode))
        {
            var books = await LoadRegimeBooksAsync(accountId, ct);
            cfg = BuildRegimeConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout, request.Rules,
                request.RegimeMode, books, request.AutopauseOverrides, accountTactics);
        }
        else
        {
            cfg = ToConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout, request.AutopauseDuringBear, profile, accountTactics, request.Rules);
        }
        var result = await HistoricBacktester.RunAsync(bars, cfg, sectorEtfs, ct);
        // Trade log stays out of the stored JSON - it can be thousands of
        // rows; the headline stats + buckets are what the UI shows.
        return JsonSerializer.Serialize(result with { TradeLog = [] }, CamelCase);
    }

    // A/B: both configs over the identical full window. Candidates carry their
    // labels ("Your dials" / "Production baseline") from queue time. When a
    // RegimeMode is set both columns replay under that regime envelope; the
    // per-regime autopause overrides apply to the user column (index 0) only,
    // so "trial Bear un-paused vs live" is a clean single-variable comparison.
    private async Task<string> RunAbAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile, int accountId,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var candidates = request.Candidates
            ?? throw new InvalidOperationException("A/B request carries no candidates.");

        var books = string.IsNullOrEmpty(request.RegimeMode) ? null : await LoadRegimeBooksAsync(accountId, ct);

        var results = new List<object>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            HistoricConfig cfg;
            if (books is not null)
            {
                var overrides = i == 0 ? request.AutopauseOverrides : null; // user column only
                cfg = BuildRegimeConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.Rules,
                    request.RegimeMode!, books, overrides, accountTactics);
            }
            else
            {
                cfg = ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, accountTactics, c.Rules);
            }
            var r = await HistoricBacktester.RunAsync(bars, cfg, sectorEtfs, ct);
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

    private async Task<IReadOnlyDictionary<MarketRegime, AccountRiskProfile>> LoadRegimeBooksAsync(int accountId, CancellationToken ct)
    {
        var books = new Dictionary<MarketRegime, AccountRiskProfile>();
        foreach (var regime in new[] { MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear, MarketRegime.Crisis })
            books[regime] = await riskProfileRepo.GetAsync(accountId, regime, ct);
        return books;
    }

    // Builds a candidate's config for a regime run. "mixed" attaches every book's
    // envelope so the engine switches per detected day; otherwise the named book
    // is forced across the whole period. Per-regime autopause overrides (user
    // column only) let a book's live autopause be flipped for the trial without
    // touching the live setting; absent = inherit the book.
    private static HistoricConfig BuildRegimeConfig(
        HistoricBacktestWeights w, decimal buyThreshold, bool excludeBreakout, HistoricTradingRules? rules,
        string regimeMode,
        IReadOnlyDictionary<MarketRegime, AccountRiskProfile> books,
        IReadOnlyDictionary<string, bool>? autopauseOverrides,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics)
    {
        bool Autopause(MarketRegime r) =>
            autopauseOverrides is not null && autopauseOverrides.TryGetValue(r.ToString(), out var v) ? v : books[r].AutopauseTrading;

        if (string.Equals(regimeMode, "mixed", StringComparison.OrdinalIgnoreCase))
        {
            var neutral = books[MarketRegime.Neutral];
            return ToConfig(w, buyThreshold, excludeBreakout, autopauseDuringBear: false, neutral, accountTactics, rules)
                with
                {
                    RegimeBooks = books.ToDictionary(
                        kv => kv.Key,
                        kv => new RegimeEnvelope(Autopause(kv.Key), kv.Value.MaxOpenPositions, kv.Value.FlatPositionPct)),
                };
        }
        var regime = Enum.TryParse<MarketRegime>(regimeMode, ignoreCase: true, out var parsed) ? parsed : MarketRegime.Neutral;
        var book = books[regime];
        return ToConfig(w, buyThreshold, excludeBreakout, autopauseDuringBear: false, book, accountTactics, rules)
            with { ForceAutopause = Autopause(regime) };
    }

    // Sweep: candidates generated around the baseline, evaluated on the train
    // window (earlier ~70%), best eligible one validated on the held-out
    // remainder it never saw. Claude explanation is best-effort - a missing
    // writeup never fails the sweep.
    private async Task<string> RunSweepAsync(
        int accountId, BacktestRun run, HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
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
        var candidates = SweepOptimizer.GenerateCandidates(baseline, request.SearchRules, productionRules, accountTactics);

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
            train, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear, profile, accountTactics, baseline.Rules), sectorEtfs, ct);
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
            var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, accountTactics, c.Rules), sectorEtfs, ct);
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
                var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, accountTactics), sectorEtfs, token);
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

        // ── Greedy second pass (coordinate descent) ──────────────────────────
        // The first pass only ever changes ONE axis at a time off the baseline
        // (weights OR a rule), so it can't find a combined best-weights +
        // best-rule config. When rule search is on, take the best WEIGHT mix
        // found above, fix it, and re-run the rule/setup search on top of it.
        // Any winner here compounds the two - the interaction the single-change
        // passes structurally miss. Same guardrails, robust ranking and holdout
        // validation apply. Skipped when no weight mix beat the baseline (there'd
        // be nothing new to refine on - the rule search already ran off baseline).
        var refinePrefixed = new List<SweepCandidateResult>();
        if (request.SearchRules)
        {
            var bestWeightMix = summaries
                .Where(s => s.MetConstraints && s.Weights != baseline.Weights)
                .OrderByDescending(s => s.RobustScorePct)
                .FirstOrDefault();
            if (bestWeightMix is not null)
            {
                var refineBase = new HistoricBacktestCandidate(
                    "Tuned weights", bestWeightMix.Weights, bestWeightMix.BuyThreshold,
                    bestWeightMix.ExcludeBreakout, bestWeightMix.AutopauseDuringBear);
                var refineCandidates = SweepOptimizer.GenerateRuleCandidates(
                    refineBase, productionRules, accountTactics, labelPrefix: "Tuned + ");

                run.TotalCandidates += refineCandidates.Count;
                await runs.UpdateAsync(run);

                foreach (var c in refineCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, accountTactics, c.Rules), sectorEtfs, ct);
                    var summary = SweepOptimizer.Summarise(c, r, trainSpy, baselineTrain.MaxDrawdownPct, baselineTrain.Trades);
                    summaries.Add(summary);
                    refinePrefixed.Add(summary);
                    trainResults[c.Label] = r;
                    logger.LogInformation("Greedy refine '{Label}': {Trades} trades, {Adj}% adjusted expectancy", c.Label, r.Trades, summary.AdjustedExpectancyPct);

                    run.CompletedCandidates++;
                    await runs.UpdateAsync(run);
                }
            }
        }

        var winnerSummary = summaries
            .Where(s => s.MetConstraints)
            .OrderByDescending(s => s.RobustScorePct)
            .FirstOrDefault()
            ?? baselineSummary; // nothing eligible - baseline "wins" by default

        var winnerSource = winnerSummary.Label == baselineSummary.Label
            ? "Baseline (no candidate improved on it)"
            : refinePrefixed.Any(s => s.Label == winnerSummary.Label)
                ? "Greedy refine (best weights + rule)"
                : bestMlSearch is not null && winnerSummary.Label == bestMlSearch.Label
                    ? "ML search (CMA-ES)"
                    : "Traditional sweep";

        // Out-of-sample validation: winner and baseline on the held-out window.
        var winnerHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(winnerSummary.Weights, winnerSummary.BuyThreshold, winnerSummary.ExcludeBreakout, winnerSummary.AutopauseDuringBear, profile, accountTactics, winnerSummary.Rules), sectorEtfs, ct);
        var baselineHoldout = winnerSummary.Label == baselineSummary.Label
            ? winnerHoldout
            : await HistoricBacktester.RunAsync(
                holdout, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear, profile, accountTactics, baseline.Rules), sectorEtfs, ct);

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
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
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
            train, ToConfig(user.Weights, user.BuyThreshold, user.ExcludeBreakout, user.AutopauseDuringBear, profile, accountTactics, user.Rules), sectorEtfs, ct);
        var userHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(user.Weights, user.BuyThreshold, user.ExcludeBreakout, user.AutopauseDuringBear, profile, accountTactics, user.Rules), sectorEtfs, ct);
        var baselineHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear, profile, accountTactics, baseline.Rules), sectorEtfs, ct);

        var validation = SweepOptimizer.BuildValidation(userTrain, userHoldout, baselineHoldout, trainSpy, holdoutSpy);
        return JsonSerializer.Serialize(new ValidateResult("validate", validation), CamelCase);
    }

    // Monte Carlo: one full-window run of the config, then bootstrap-resample
    // its trade log to measure how much of the result is trade ORDER luck
    // (sequence risk) versus trade quality - the complement to the
    // train/holdout validate, which measures window (period) luck.
    private static async Task<string> RunMonteCarloAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var cfg = ToConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout,
            request.AutopauseDuringBear, profile, accountTactics, request.Rules);
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

    // Setup-contribution (leave-one-out ablation): measures each setup's MARGINAL
    // effect on the whole strategy rather than its noisy standalone average. Runs
    // the all-setups baseline, then re-runs excluding one setup at a time, on both
    // the train and held-out windows. A setup's marginal = baseline expectancy −
    // expectancy-without-it: positive means the setup ADDS edge (removing it
    // hurts), negative means it's a DRAG (removing it helps). Reporting both
    // windows lets the user trust only setups whose sign is consistent across
    // periods. Uses production dials as the base; ~2 + 2×setups backtests, all on
    // the same in-memory bars (no extra DB load).
    private async Task<string> RunSetupAblationAsync(
        BacktestRun run, HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars,
        AccountRiskProfile profile, IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var baseline = request.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("Ablation request carries no baseline candidate.");

        var (train, holdout) = SweepOptimizer.SplitBars(bars, HistoricBacktester.WarmupBars);
        var trainSpy = train["SPY"];
        var holdoutSpy = holdout["SPY"];

        var setups = accountTactics.Keys.OrderBy(s => s.ToString()).ToList();
        run.TotalCandidates = 2 + setups.Count * 2; // baseline (2 windows) + each setup (2 windows)
        run.CompletedCandidates = 0;
        await runs.UpdateAsync(run);

        // A run with the given setups excluded (null = all setups = the baseline).
        async Task<(HistoricResult Result, decimal Adjusted)> RunAsync(
            Dictionary<string, DailyBar[]> window, DailyBar[] spy, SetupType? exclude)
        {
            var rules = exclude is { } s
                ? new HistoricTradingRules(ExcludedSetups: [s.ToString()])
                : baseline.Rules;
            var cfg = ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                baseline.AutopauseDuringBear, profile, accountTactics, rules);
            var r = await HistoricBacktester.RunAsync(window, cfg, sectorEtfs, ct);
            run.CompletedCandidates++;
            await runs.UpdateAsync(run);
            return (r, SweepOptimizer.AdjustedExpectancy(r, spy));
        }

        var (baseTrainR, baseTrainAdj) = await RunAsync(train, trainSpy, null);
        var (baseHoldR, baseHoldAdj) = await RunAsync(holdout, holdoutSpy, null);

        var rows = new List<object>();
        foreach (var setup in setups)
        {
            ct.ThrowIfCancellationRequested();
            var (wTrainR, wTrainAdj) = await RunAsync(train, trainSpy, setup);
            var (wHoldR, wHoldAdj) = await RunAsync(holdout, holdoutSpy, setup);
            rows.Add(new
            {
                setup = setup.ToString(),
                // Marginal contribution = baseline − without. + adds edge, − is a drag.
                marginalTrainAdj = Math.Round(baseTrainAdj - wTrainAdj, 3),
                marginalHoldoutAdj = Math.Round(baseHoldAdj - wHoldAdj, 3),
                holdoutAdjWithout = Math.Round(wHoldAdj, 3),
                holdoutTradesWithout = wHoldR.Trades,
                holdoutMaxDrawdownWithout = wHoldR.MaxDrawdownPct,
                // Consistent sign across both windows = trustworthy verdict.
                consistent = Math.Sign(baseTrainAdj - wTrainAdj) == Math.Sign(baseHoldAdj - wHoldAdj),
            });
            logger.LogInformation("Ablation '{Setup}': marginal train {T}% / holdout {H}%", setup, Math.Round(baseTrainAdj - wTrainAdj, 3), Math.Round(baseHoldAdj - wHoldAdj, 3));
        }

        var result = new
        {
            mode = "ablation",
            baselineTrainAdjustedPct = Math.Round(baseTrainAdj, 3),
            baselineHoldoutAdjustedPct = Math.Round(baseHoldAdj, 3),
            baselineHoldoutTrades = baseHoldR.Trades,
            baselineHoldoutMaxDrawdownPct = baseHoldR.MaxDrawdownPct,
            setups = rows,
        };
        return JsonSerializer.Serialize(result, CamelCase);
    }

    // Regime comparison: runs the account's production strategy over the FULL
    // period five ways - each regime book forced across the whole history, plus
    // "Mixed" where the engine switches book by the regime detected at each day
    // (docs regime-lab). Answers "is a regime mix worth it, or is one book best?"
    // One candle load, five in-memory engine passes (no extra DB/Claude cost).
    // Crisis is price-structure-undetectable without historical VIX, so in Mixed
    // it never fires - the forced-Crisis column still stress-tests that book.
    private async Task<string> RunRegimeComparisonAsync(
        BacktestRun run, HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars,
        int accountId, IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var baseline = request.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("Regime comparison carries no baseline candidate.");

        var regimes = new[] { MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear, MarketRegime.Crisis };
        var books = new Dictionary<MarketRegime, AccountRiskProfile>();
        foreach (var regime in regimes)
            books[regime] = await riskProfileRepo.GetAsync(accountId, regime, ct);

        run.TotalCandidates = regimes.Length + 1; // four forced + Mixed
        run.CompletedCandidates = 0;
        await runs.UpdateAsync(run);

        static object Row(string mode, HistoricResult r) => new
        {
            mode,
            trades = r.Trades,
            winRate = r.WinRate,                 // fraction; UI formats as a percent
            expectancyPct = Math.Round(r.ExpectancyPct, 3),
            totalReturnPct = Math.Round(r.TotalReturnPct, 1),
            maxDrawdownPct = Math.Round(r.MaxDrawdownPct, 1),
            calmarRatio = Math.Round(r.CalmarRatio, 2),
        };

        var rows = new List<object>();

        // Forced single-book runs: the whole period under one regime's envelope
        // (its autopause, position cap and flat size), same strategy throughout.
        foreach (var regime in regimes)
        {
            ct.ThrowIfCancellationRequested();
            var book = books[regime];
            // Forcing a regime means we KNOW it - a book that autopauses pauses
            // the whole period (no SPY-200 proxy). autopauseDuringBear:false so
            // RegimeFilter stays off; ForceAutopause carries the book's toggle.
            var cfg = ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                autopauseDuringBear: false, book, accountTactics, baseline.Rules)
                with { ForceAutopause = book.AutopauseTrading };
            var r = await HistoricBacktester.RunAsync(bars, cfg, sectorEtfs, ct);
            rows.Add(Row($"Force {regime}", r));
            run.CompletedCandidates++;
            await runs.UpdateAsync(run);
            logger.LogInformation("Regime compare Force {Regime}: {Trades} trades, {Exp}%/trade, {Ret}% total",
                regime, r.Trades, Math.Round(r.ExpectancyPct, 2), Math.Round(r.TotalReturnPct, 1));
        }

        // Mixed: envelope switches per simulated day by the detected regime. Base
        // config from Neutral for the regime-invariant strategy fields; every
        // book's envelope supplied so the engine can pick per day.
        var neutral = books[MarketRegime.Neutral];
        var mixedCfg = ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
            neutral.AutopauseTrading, neutral, accountTactics, baseline.Rules)
            with
        {
            RegimeBooks = books.ToDictionary(
                kv => kv.Key,
                kv => new RegimeEnvelope(kv.Value.AutopauseTrading, kv.Value.MaxOpenPositions, kv.Value.FlatPositionPct)),
        };
        var mixed = await HistoricBacktester.RunAsync(bars, mixedCfg, sectorEtfs, ct);
        rows.Add(Row("Mixed (regime-switch)", mixed));
        run.CompletedCandidates++;
        await runs.UpdateAsync(run);

        var result = new
        {
            mode = "regime",
            spyReturnPct = Math.Round(mixed.SpyReturnPct, 1),
            rows,
        };
        return JsonSerializer.Serialize(result, CamelCase);
    }
}
