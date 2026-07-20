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

    // One backtest at a time per host instance. Each job loads the ENTIRE
    // historic candle store into memory plus the engine's working set, so the
    // default Service Bus concurrency running "queue three at once" (the Lab's
    // Run-all-checks button) OOM'd the worker. Deliberately NOT host.json
    // maxConcurrentCalls - that's global and would throttle the research
    // queues too. Waiting here is safe: the lock auto-renews for 2.5h and a
    // single run takes minutes.
    private static readonly SemaphoreSlim BacktestGate = new(1, 1);

    [Function("BacktestConsumer")]
    public async Task Run(
        [ServiceBusTrigger("backtest-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        await BacktestGate.WaitAsync(ct);
        try
        {
            await RunSerializedAsync(messageBody, ct);
        }
        finally
        {
            BacktestGate.Release();
        }
    }

    private async Task RunSerializedAsync(string messageBody, CancellationToken ct)
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
            // with today's live regime - UNLESS the Default master book is on,
            // in which case every sim uses it (it governs live too).
            var defaultOn = await riskProfileRepo.IsDefaultRegimeEnabledAsync(message.AccountId, ct);
            var profile = await riskProfileRepo.GetAsync(
                message.AccountId, defaultOn ? MarketRegime.Default : MarketRegime.Neutral, ct);

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

            // Evidence stamping (ab / validate / montecarlo): the fingerprint of
            // the RESOLVED user config, INCLUDING the live regime envelopes
            // when the account trades Mixed - matched against the sender's
            // live-settings fingerprint by the strategy-share gate. A run only
            // gets stamped when it MIRRORS how the account actually operates
            // (MirrorsLiveAsync); the envelopes are hashed from the LIVE books
            // (not the form's rounded copies) so both sides derive identical
            // values. Other modes test many configs - no stamp.
            run.ConfigFingerprint = await ComputeEvidenceFingerprintAsync(
                request, defaultOn, profile, accountTactics, message.AccountId, ct);

            run.ResultJson = request.Mode switch
            {
                "ab" => await RunAbAsync(request, bars, profile, message.AccountId, accountTactics, sectorEtfs, ct),
                "sweep" => await RunSweepAsync(message.AccountId, run, request, bars, profile, accountTactics, sectorEtfs, ct),
                "validate" => await RunValidateAsync(request, bars, profile, message.AccountId, accountTactics, sectorEtfs, ct),
                "montecarlo" => await RunMonteCarloAsync(request, bars, profile, message.AccountId, accountTactics, sectorEtfs, ct),
                "ablation" => await RunSetupAblationAsync(run, request, bars, profile, accountTactics, sectorEtfs, ct),
                "regime" => await RunRegimeComparisonAsync(run, request, bars, message.AccountId, accountTactics, sectorEtfs, ct),
                "setupsearch" => await RunSetupSearchAsync(run, request, bars, profile, accountTactics, sectorEtfs, ct),
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

    // True when an A/B run replays the account's LIVE trading behaviour: the
    // fixed Default book when the master switch is on (plain or forced-Default
    // run), otherwise the Mixed per-regime switching that live actually does.
    // The Lab's Mixed form ALWAYS sends the four envelope levers per regime
    // (seeded from the live books), so "no overrides" can never happen from
    // the UI - instead each sent override must EQUAL the live book's value.
    // The UI rounds fraction fields to 0.1%, hence the small tolerance.
    // Resolves the candidate config an evidence run tested (ab/validate ->
    // candidates[0], montecarlo -> the request itself), checks it mirrors the
    // live trading frame, and fingerprints it with the live regime envelopes
    // attached when the Default master book is off. Null = not evidence.
    private async Task<string?> ComputeEvidenceFingerprintAsync(
        HistoricBacktestRequest request, bool defaultOn, AccountRiskProfile profile,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics, int accountId, CancellationToken ct)
    {
        HistoricBacktestCandidate? candidate = request.Mode switch
        {
            "ab" when request.Candidates is { Count: > 0 } => request.Candidates[0],
            "validate" when request.Candidates is { Count: 2 } => request.Candidates[0],
            "montecarlo" => new HistoricBacktestCandidate(
                "mc", request.Weights, request.BuyThreshold, request.ExcludeBreakout,
                request.AutopauseDuringBear, request.Rules),
            _ => null,
        };
        if (candidate is null) return null;
        if (!await MirrorsLiveAsync(request, defaultOn, accountId, ct)) return null;

        var cfg = ToConfig(candidate.Weights, candidate.BuyThreshold, candidate.ExcludeBreakout,
            candidate.AutopauseDuringBear, profile, accountTactics, candidate.Rules);
        if (!defaultOn)
            cfg = BacktestConfigFactory.WithLiveRegimeBooks(cfg, await LoadRegimeBooksAsync(accountId, ct));
        return ConfigFingerprint.Compute(cfg);
    }

    private async Task<bool> MirrorsLiveAsync(
        HistoricBacktestRequest request, bool defaultOn, int accountId, CancellationToken ct)
    {
        var mode = request.RegimeMode;
        if (defaultOn)
            return string.IsNullOrEmpty(mode) || string.Equals(mode, "default", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(mode, "mixed", StringComparison.OrdinalIgnoreCase))
            return false;
        if (request.RegimeOverrides is null || request.RegimeOverrides.Count == 0)
            return true;

        static bool Near(decimal? a, decimal b) => a is null || Math.Abs(a.Value - b) <= 0.0005m;
        foreach (var (regimeName, o) in request.RegimeOverrides)
        {
            if (!Enum.TryParse<MarketRegime>(regimeName, ignoreCase: true, out var regime))
                return false;
            var book = await riskProfileRepo.GetAsync(accountId, regime, ct);
            if (o.Autopause is not null && o.Autopause != book.AutopauseTrading) return false;
            if (!Near(o.LockedCapitalPct, book.LockedCapitalPct)) return false;
            if (!Near(o.PositionFraction, book.FlatPositionPct)) return false;
            if (o.MaxOpenPositions is not null && o.MaxOpenPositions != book.MaxOpenPositions) return false;
        }
        return true;
    }

    // Gate-weight components an optimizer run holds FIXED at the baseline
    // value (the UI's lock checkboxes). Names are the camelCase dial keys;
    // unknown names are ignored like ParseSetups does.
    private static int[]? ParseLockedComponents(List<string>? names)
    {
        if (names is null || names.Count == 0) return null;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["rsi"] = 0, ["macd"] = 1, ["volume"] = 2,
            ["setupQuality"] = 3, ["relativeStrength"] = 4, ["priceLevel"] = 5,
        };
        var idx = names.Where(map.ContainsKey).Select(n => map[n]).Distinct().ToArray();
        return idx.Length == 0 ? null : idx;
    }

    // Config resolution lives in BacktestConfigFactory (shared with the API's
    // strategy-share fingerprinting) - thin delegates keep the call sites here
    // unchanged.
    private static HistoricConfig ToConfig(
        HistoricBacktestWeights w, decimal buyThreshold, bool excludeBreakout, bool autopauseDuringBear,
        AccountRiskProfile profile, IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        HistoricTradingRules? rules = null) =>
        BacktestConfigFactory.ToConfig(w, buyThreshold, excludeBreakout, autopauseDuringBear, profile, accountTactics, rules);

    private static IReadOnlyCollection<SwingTrader.Core.Enums.SetupType>? ParseSetups(List<string>? names) =>
        BacktestConfigFactory.ParseSetups(names);

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
                request.RegimeMode, books, request.RegimeOverrides, accountTactics);
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
                var overrides = i == 0 ? request.RegimeOverrides : null; // user column only
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
        var regimes = new[] { MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear, MarketRegime.Crisis };
        // The Default master book is always loaded under its own key so a
        // forced-"Default" A/B run can replay under it on demand (whether or not
        // it's the live master). Mixed excludes it - the engine never DETECTS
        // Default, so it's only ever a forced choice.
        var defBook = await riskProfileRepo.GetAsync(accountId, MarketRegime.Default, ct);

        // Default master book on: every detectable regime resolves to it, so
        // regime-switching (Mixed) and forced-regime runs all replay under it.
        if (await riskProfileRepo.IsDefaultRegimeEnabledAsync(accountId, ct))
        {
            var d = regimes.ToDictionary(r => r, _ => defBook);
            d[MarketRegime.Default] = defBook;
            return d;
        }
        var books = new Dictionary<MarketRegime, AccountRiskProfile>();
        foreach (var regime in regimes)
            books[regime] = await riskProfileRepo.GetAsync(accountId, regime, ct);
        books[MarketRegime.Default] = defBook;
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
        IReadOnlyDictionary<string, RegimeExposureOverride>? overrides,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics)
    {
        RegimeExposureOverride? Ov(MarketRegime r) =>
            overrides is not null && overrides.TryGetValue(r.ToString(), out var o) ? o : null;

        // The regime's live envelope with any user override layered on (a null
        // field inherits the book) - the "3 forms" editor under Mixed.
        RegimeEnvelope Envelope(MarketRegime r)
        {
            var b = books[r];
            var o = Ov(r);
            return new RegimeEnvelope(
                o?.Autopause ?? b.AutopauseTrading,
                o?.MaxOpenPositions ?? b.MaxOpenPositions,
                o?.PositionFraction ?? b.FlatPositionPct,
                o?.LockedCapitalPct ?? b.LockedCapitalPct);
        }

        if (string.Equals(regimeMode, "mixed", StringComparison.OrdinalIgnoreCase))
        {
            var neutral = books[MarketRegime.Neutral];
            return ToConfig(w, buyThreshold, excludeBreakout, autopauseDuringBear: false, neutral, accountTactics, rules)
                with
                {
                    // Default is never detected day-to-day, so it's excluded from
                    // the per-day switch set (it's only a forced-regime choice).
                    RegimeBooks = books.Keys.Where(r => r != MarketRegime.Default).ToDictionary(r => r, Envelope),
                };
        }
        // Forced single regime: the exit/probation rules come from the uniform
        // panel (rules); only Autopause is taken from the per-regime override.
        var regime = Enum.TryParse<MarketRegime>(regimeMode, ignoreCase: true, out var parsed) ? parsed : MarketRegime.Neutral;
        var book = books[regime];
        return ToConfig(w, buyThreshold, excludeBreakout, autopauseDuringBear: false, book, accountTactics, rules)
            with { ForceAutopause = Ov(regime)?.Autopause ?? book.AutopauseTrading };
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
            MomentumHealthThreshold: profile.MomentumHealthThreshold,
            PositionFraction: profile.FlatPositionPct,
            LockedCapitalPct: profile.LockedCapitalPct);
        var lockedIndices = ParseLockedComponents(request.LockedComponents);
        var candidates = SweepOptimizer.GenerateCandidates(baseline, request.SearchRules, productionRules, accountTactics, lockedIndices);

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
                // c.Rules rides in (the ML candidates are `baseline with {...}`
                // so they carry the baseline's rules) - dropping it here made
                // the ML pool trade live-disabled setups while the traditional
                // pool honoured the exclusions, and an ML winner was then
                // validated under a different config than it trained on.
                var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, accountTactics, c.Rules), sectorEtfs, token);
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
            maxParallelism: Math.Clamp(Environment.ProcessorCount, 1, 4),
            lockedIndices: lockedIndices);

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
                // Rules carried from the best weight mix (= the baseline's rules,
                // since weight variants share them) - without this the refine
                // pass dropped the account's excluded-setups list and every
                // "Tuned + ..." candidate silently re-admitted disabled setups.
                var refineBase = new HistoricBacktestCandidate(
                    "Tuned weights", bestWeightMix.Weights, bestWeightMix.BuyThreshold,
                    bestWeightMix.ExcludeBreakout, bestWeightMix.AutopauseDuringBear,
                    Rules: bestWeightMix.Rules);
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
    private async Task<string> RunValidateAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        int accountId,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var candidates = request.Candidates is { Count: 2 }
            ? request.Candidates
            : throw new InvalidOperationException("Validate request needs exactly [user, baseline] candidates.");
        var user = candidates[0];
        var baseline = candidates[1];

        // Regime-aware (20 Jul 2026): a Mixed request validates under the same
        // per-day regime-switching envelopes the A/B sim (and live) use, so
        // the holdout verdict describes the world actually traded. Overrides
        // apply to the user column only; the baseline replays the live books.
        var books = string.IsNullOrEmpty(request.RegimeMode) ? null : await LoadRegimeBooksAsync(accountId, ct);
        HistoricConfig Cfg(HistoricBacktestCandidate c, bool isUser) => books is null
            ? ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear, profile, accountTactics, c.Rules)
            : BuildRegimeConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.Rules,
                request.RegimeMode!, books, isUser ? request.RegimeOverrides : null, accountTactics);

        var (train, holdout) = SweepOptimizer.SplitBars(bars, HistoricBacktester.WarmupBars);
        var trainSpy = train["SPY"];
        var holdoutSpy = holdout["SPY"];

        var userTrain = await HistoricBacktester.RunAsync(train, Cfg(user, true), sectorEtfs, ct);
        var userHoldout = await HistoricBacktester.RunAsync(holdout, Cfg(user, true), sectorEtfs, ct);
        var baselineHoldout = await HistoricBacktester.RunAsync(holdout, Cfg(baseline, false), sectorEtfs, ct);

        var validation = SweepOptimizer.BuildValidation(userTrain, userHoldout, baselineHoldout, trainSpy, holdoutSpy);
        return JsonSerializer.Serialize(new ValidateResult("validate", validation), CamelCase);
    }

    // Monte Carlo: one full-window run of the config, then bootstrap-resample
    // its trade log to measure how much of the result is trade ORDER luck
    // (sequence risk) versus trade quality - the complement to the
    // train/holdout validate, which measures window (period) luck.
    private async Task<string> RunMonteCarloAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile,
        int accountId,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        // Regime-aware (20 Jul 2026): same frame as validate above - the
        // drawdown-to-budget number must describe the envelopes actually
        // traded, not a single-book approximation of them.
        var cfg = string.IsNullOrEmpty(request.RegimeMode)
            ? ToConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout,
                request.AutopauseDuringBear, profile, accountTactics, request.Rules)
            : BuildRegimeConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout, request.Rules,
                request.RegimeMode!, await LoadRegimeBooksAsync(accountId, ct), request.RegimeOverrides, accountTactics);
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
            // +30k adaptive-thinking headroom (20 Jul 2026): Sonnet 5's
            // thinking shares max_tokens with the answer and can starve it.
            var response = await claude.SendMessageAsync(new ClaudeRequest(
                cfg.RefinementModel ?? cfg.PremiumModel, cfg.MaxTokens + 30000,
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
    // Crisis fires in Mixed when the bar set carries CBOE VIX history (synced
    // by CandleSync since 17 Jul 2026); before the first VIX sync it degrades
    // to price-structure-only and never fires.
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

        run.TotalCandidates = regimes.Length + 2; // four forced + Force Default + Mixed
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

        // Force Default: the master override book across the whole period - what
        // live does when Default is on (or would, if you turned it on), so the
        // comparison stays honest whether or not Default currently governs.
        ct.ThrowIfCancellationRequested();
        var defaultBook = await riskProfileRepo.GetAsync(accountId, MarketRegime.Default, ct);
        var defCfg = ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
            autopauseDuringBear: false, defaultBook, accountTactics, baseline.Rules)
            with { ForceAutopause = defaultBook.AutopauseTrading };
        rows.Add(Row("Force Default", await HistoricBacktester.RunAsync(bars, defCfg, sectorEtfs, ct)));
        run.CompletedCandidates++;
        await runs.UpdateAsync(run);

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
                kv => new RegimeEnvelope(kv.Value.AutopauseTrading, kv.Value.MaxOpenPositions, kv.Value.FlatPositionPct, kv.Value.LockedCapitalPct)),
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

    // Setup-combination search: brute-forces EVERY non-empty combination of the
    // account's setups (2^N − 1 - 31 for the five live setups) over the full
    // period, holding the current live dials and governing risk book fixed, and
    // ranks them by market-adjusted expectancy. Answers "which mix of setups
    // should I actually be trading?" - the complement of the ablation view,
    // which only removes one setup at a time. A long run by design (one engine
    // pass per combination), but a single candle load with no DB/Claude cost
    // per pass. The combination matching what's live now is flagged so the user
    // can see how their current selection ranks against the field.
    private async Task<string> RunSetupSearchAsync(
        BacktestRun run, HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars,
        AccountRiskProfile profile, IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        IReadOnlyDictionary<string, string>? sectorEtfs, CancellationToken ct)
    {
        var baseline = request.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("Setup search carries no baseline candidate.");

        // The universe searched is every setup the account has tactics for -
        // deliberately IGNORING the current live on/off switches, since the whole
        // point is to discover whether a currently-disabled setup earns its place
        // (or an enabled one should go). Deterministic order so masks are stable.
        var setups = accountTactics.Keys.OrderBy(s => s.ToString()).ToList();
        var n = setups.Count;
        if (n == 0) throw new InvalidOperationException("No setups available to search.");

        // The setups live right now (baseline excludes the disabled ones), used
        // only to flag the matching row - not to constrain the search.
        var liveExcluded = ParseSetups(baseline.Rules?.ExcludedSetups)?.ToHashSet() ?? [];
        var liveEnabled = setups.Where(s => !liveExcluded.Contains(s)).ToHashSet();

        var spy = bars.TryGetValue("SPY", out var spyBars) ? spyBars : Array.Empty<DailyBar>();

        run.TotalCandidates = (1 << n) - 1; // every non-empty subset
        run.CompletedCandidates = 0;
        await runs.UpdateAsync(run);

        var scored = new List<(decimal Adjusted, object Row)>();
        for (var mask = 1; mask < (1 << n); mask++)
        {
            ct.ThrowIfCancellationRequested();

            var included = new List<SetupType>();
            for (var bit = 0; bit < n; bit++)
                if ((mask & (1 << bit)) != 0) included.Add(setups[bit]);
            var excluded = setups.Where(s => !included.Contains(s)).ToList();

            // Only WHICH setups trade varies - dials, thresholds, per-setup
            // tactics and the governing risk book are the live ones throughout.
            var rules = (baseline.Rules ?? new HistoricTradingRules()) with
            {
                ExcludedSetups = excluded.Select(s => s.ToString()).ToList(),
            };
            var cfg = ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout,
                baseline.AutopauseDuringBear, profile, accountTactics, rules);
            var r = await HistoricBacktester.RunAsync(bars, cfg, sectorEtfs, ct);
            var adjusted = SweepOptimizer.AdjustedExpectancy(r, spy);

            scored.Add((adjusted, new
            {
                setups = included.Select(s => s.ToString()).ToList(),
                setupCount = included.Count,
                isCurrentLive = included.Count == liveEnabled.Count && included.All(liveEnabled.Contains),
                trades = r.Trades,
                winRate = r.WinRate,               // fraction; UI formats as a percent
                expectancyPct = Math.Round(r.ExpectancyPct, 3),
                adjustedPct = Math.Round(adjusted, 3),
                totalReturnPct = Math.Round(r.TotalReturnPct, 1),
                maxDrawdownPct = Math.Round(r.MaxDrawdownPct, 1),
                calmarRatio = Math.Round(r.CalmarRatio, 2),
            }));

            run.CompletedCandidates++;
            await runs.UpdateAsync(run);
        }

        // Best-first by market-adjusted expectancy - the same metric the
        // optimizer ranks on, so the winner here is comparable to a sweep's.
        var ranked = scored.OrderByDescending(s => s.Adjusted).Select(s => s.Row).ToList();

        logger.LogInformation("Setup search for run {RunId}: evaluated {Count} combinations of {N} setups",
            run.Id, ranked.Count, n);

        var result = new
        {
            mode = "setupsearch",
            spyReturnPct = spy.Length > 0 ? Math.Round((spy[^1].Close - spy[0].Close) / spy[0].Close * 100m, 1) : 0m,
            setupsAvailable = setups.Select(s => s.ToString()).ToList(),
            rows = ranked,
        };
        return JsonSerializer.Serialize(result, CamelCase);
    }
}
