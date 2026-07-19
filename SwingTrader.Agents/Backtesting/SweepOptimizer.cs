using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// The Optimizer-tab sweep: generate a capped set of candidate dial configs
// around the production baseline, evaluate them on a TRAIN window, pick the
// best by market-adjusted expectancy (subject to guardrails), then validate
// the winner on the HELD-OUT remainder it never saw. The validation split is
// a hard requirement, not decoration: with 8 free weights and a few hundred
// trades, an unvalidated sweep will always "find" a config that looks great
// on the data it was tuned on.

public sealed record SweepCandidateResult(
    string Label,
    HistoricBacktestWeights Weights,
    decimal BuyThreshold,
    bool ExcludeBreakout,
    bool AutopauseDuringBear,
    int Trades,
    decimal WinRate,
    decimal ExpectancyPct,               // raw avg return/trade on the train window
    decimal AdjustedExpectancyPct,       // avg (trade return - SPY return over same days)
    decimal ProfitFactor,
    decimal MaxDrawdownPct,
    decimal TotalReturnPct,
    bool MetConstraints,
    string? RejectionReason,             // why this candidate couldn't win (null if eligible)
    // The score every eligible candidate is actually RANKED on (across both
    // search pools and for the final winner): the worse of the two
    // train-window halves, each discounted to a lower confidence bound. It's
    // deliberately more pessimistic than AdjustedExpectancyPct so a big mean
    // from a small/one-regime sample can't win on luck; the headline
    // AdjustedExpectancyPct is kept alongside it for display.
    decimal RobustScorePct = 0m,
    // Trading-rule overrides this candidate tested (only set by the "search
    // for optimal trading rules" sweep variants; null = production rules).
    // Carried onto the winner so Optimizer History can re-apply the rules
    // alongside the weights. Defaulted (like RobustScorePct) so pre-existing
    // stored result JSON keeps deserializing.
    HistoricTradingRules? Rules = null,
    // The full set of setups this candidate excluded (SetupType names), so
    // "Test winner in A/B" restores the whole selection - not just breakout.
    // Derived from Rules.ExcludedSetups or the legacy ExcludeBreakout toggle.
    List<string>? ExcludedSetups = null);

public sealed record SweepValidation(
    HistoricResult Train,                // winner's full result on the train window
    HistoricResult Holdout,              // winner's full result on the held-out window
    decimal TrainAdjustedExpectancyPct,
    decimal HoldoutAdjustedExpectancyPct,
    HistoricResult BaselineHoldout,      // production baseline on the same held-out window
    decimal BaselineHoldoutAdjustedExpectancyPct,
    bool HeldUp,                         // false = the edge collapsed out-of-sample
    string Verdict);                     // plain-English interpretation of the above

// Result of an on-demand out-of-sample validation of ONE configuration (the
// A/B tab's "Validate out-of-sample" button): the same train/holdout split
// and hold-up verdict the optimizer applies to its sweep winner, applied to
// dials the user tuned by hand - which are in-sample by construction until
// they pass this.
public sealed record ValidateResult(
    string Mode,                         // "validate" - discriminator for the UI
    SweepValidation Validation);

public sealed record SweepResult(
    string Mode,                         // "sweep" - discriminator for the UI
    SweepCandidateResult Baseline,
    SweepCandidateResult Winner,
    SweepValidation Validation,
    List<SweepCandidateResult> Candidates,
    string? Explanation,                 // Claude writeup (null if unavailable)
    // Head-to-head: the best eligible candidate found by each search method,
    // and which one actually produced the overall Winner above - so the UI
    // can show whether the deterministic/random sweep or the CMA-ES-guided
    // search found the better config, not just report the winner in isolation.
    SweepCandidateResult? BestTraditional = null,
    SweepCandidateResult? BestMlSearch = null,
    string? WinnerSource = null);

public static class SweepOptimizer
{
    // ~70% of the calendar to optimize on, the rest held out for validation.
    public const decimal TrainFraction = 0.70m;

    // Guardrails on what may win: a config that wins on a handful of trades is
    // noise, and one that wins by tolerating a much deeper drawdown isn't a
    // better strategy, just a riskier one.
    public const int MinTrainTrades = 40;
    public const decimal MaxDrawdownCeilingFactor = 1.25m;

    // The first optimizer run's winner sat EXACTLY on MinTrainTrades - 40
    // trades where the baseline took 195 - and collapsed out-of-sample. A
    // fixed floor is a boundary the search learns to ride: fewer trades
    // makes a lucky high average easier. So candidates must also trade at a
    // comparable rate to the baseline - a config trading 5x less isn't a
    // tuned version of the strategy, it's a different (nearly inactive) one.
    public const decimal MinTradeRateVsBaselineFactor = 0.5m;

    // Candidates are RANKED on a lower confidence bound - mean adjusted
    // expectancy minus this many standard errors - rather than the raw mean,
    // so a big average from a small, noisy sample is discounted by its own
    // uncertainty instead of winning on luck. ~1.5 SE is roughly a one-sided
    // 93% bound: harsh enough to kill 40-lucky-trades candidates, loose
    // enough not to bury genuinely better configs with normal variance.
    public const decimal LcbStdErrMultiplier = 1.5m;

    // A winner "holds up" if its held-out adjusted expectancy keeps at least
    // half its train value AND beats the baseline on the same held-out window.
    public const decimal HoldupRetentionFactor = 0.5m;

    // Weight-array indices, matching HistoricBacktestWeights field order. All
    // six gate weights are searched (sentiment and fundamental momentum are no
    // longer part of the gate - they drive the live Forward score instead).
    // Internal (not private) so MlSweepOptimizer's CMA-ES search shares the
    // exact same renormalisation as the deterministic sweep.
    internal static readonly int[] LiveIndices = [0, 1, 2, 3, 4, 5]; // RSI, MACD, Volume, Setup, RelStrength, PriceLevel

    // Deterministic candidate generation: production baseline + variations
    // that redistribute weight ONLY among the live components, holding the
    // dead two at their production values. Shifting weight into/out of a
    // dead component just dilutes conviction scores toward the neutral
    // midpoint - a candidate could "win" purely because sentiment is dead in
    // the backtest, a conclusion that wouldn't transfer to production where
    // sentiment has real data. Same reasoning as the locked dials in the A/B
    // tab's historic mode. Also deliberately NOT seeded from own-data
    // optimizer output - own-data evaluation is censored (only trades
    // production weights took) and would pipe its overfit into this, the
    // honest pipeline.
    // Total candidates per sweep. Each engine run costs a few seconds, so
    // ~400 keeps the job in the 20-40 minute range while searching the live
    // simplex far more densely than the original ~25 (bumped from 200).
    public const int TargetCandidateCount = 400;

    public static List<HistoricBacktestCandidate> GenerateCandidates(
        HistoricBacktestCandidate baseline, bool searchRules = false, HistoricTradingRules? productionRules = null,
        // The account's live per-setup tactics, so the rule search can nudge a
        // single setup's guide-hold off its real current value. Null = skip the
        // per-setup search (older callers / tests).
        IReadOnlyDictionary<SetupType, HistoricSetupTactics>? accountTactics = null,
        // Gate-weight indices the user LOCKED at the baseline value (the UI's
        // lock checkboxes, e.g. "Setup quality stays 30%"). Locked dials are
        // never nudged, shifted or randomised; the remaining budget is
        // redistributed among the unlocked dials only.
        int[]? lockedIndices = null)
    {
        var candidates = new List<HistoricBacktestCandidate> { baseline with { Label = "Production baseline" } };
        var names = new[] { "RSI", "MACD", "Volume", "Setup quality", "Relative strength", "Price level" };
        var baseArr = ToArray(baseline.Weights);
        var live = lockedIndices is { Length: > 0 }
            ? LiveIndices.Where(i => !lockedIndices.Contains(i)).ToArray()
            : LiveIndices;
        var liveBudget = live.Sum(i => baseArr[i]); // what the swept (unlocked) dials must always sum to

        // Single-dial nudges: ±5pp and ±10pp on each LIVE weight, other live
        // weights renormalised to keep the live total (and therefore the
        // overall sum) unchanged.
        foreach (var i in live)
        {
            foreach (var delta in new[] { 0.05m, -0.05m, 0.10m, -0.10m })
            {
                var arr = (decimal[])baseArr.Clone();
                var nv = arr[i] + delta;
                if (nv < 0.02m || nv > 0.45m) continue;
                arr[i] = nv;
                RenormaliseLive(arr, liveBudget, live);
                candidates.Add(baseline with
                {
                    Label = $"{names[i]} {(delta > 0 ? "+" : "−")}{Math.Abs(delta) * 100:0}pp",
                    Weights = FromArray(arr),
                });
            }
        }

        // Buy-threshold variants on the baseline mix.
        foreach (var t in new[] { -1.5m, -1.0m, -0.5m, -0.25m, 0.25m, 0.5m, 1.0m, 1.5m })
        {
            var nt = Math.Clamp(baseline.BuyThreshold + t, 3.0m, 9.0m);
            if (nt != baseline.BuyThreshold && candidates.All(c => c.BuyThreshold != nt || c.Weights != baseline.Weights))
                candidates.Add(baseline with { Label = $"Buy threshold {nt:0.0}", BuyThreshold = nt });
        }

        // Autopause is no longer swept here: it's a per-regime-book decision
        // (Risk Management / the Regimes comparison), not a single-book weight
        // lever. The old SPY-below-200 proxy toggle diverged from how live now
        // pauses (per detected regime), so it's retired from the sweep.

        // Structural variants: small nudges rarely separate from noise on a
        // finite trade sample, so also try genuinely different LIVE mixes -
        // each spreads/concentrates the same live budget differently, dead
        // dials untouched. Proportions follow LiveIndices order: RSI, MACD,
        // Volume, Setup quality, Relative strength, Price level.
        if (live.Length == LiveIndices.Length)
        {
            var structural = new (string Label, decimal[] Proportions)[]
            {
                ("Equal live weights", [1m / 6, 1m / 6, 1m / 6, 1m / 6, 1m / 6, 1m / 6]),
                ("Momentum tilt (MACD/Volume heavy)", [0.10m, 0.28m, 0.28m, 0.10m, 0.14m, 0.10m]),
                ("Pattern tilt (RSI/Setup heavy)", [0.26m, 0.08m, 0.10m, 0.30m, 0.08m, 0.18m]),
                ("Structure tilt (RelStrength/PriceLevel heavy)", [0.12m, 0.10m, 0.12m, 0.16m, 0.25m, 0.25m]),
            };
            foreach (var (label, proportions) in structural)
                candidates.Add(MakeLiveMix(baseline, baseArr, liveBudget, label, proportions, live));
        }
        else if (live.Length > 1)
        {
            // With locks the named tilts don't apply (their proportions cover
            // all six dials) - one even spread of the unlocked budget stands in
            // as the "structurally different" probe.
            var equal = Enumerable.Repeat(1m / live.Length, live.Length).ToArray();
            candidates.Add(MakeLiveMix(baseline, baseArr, liveBudget, "Equal unlocked weights", equal, live));
        }

        // Pair trades among live components: shift 4pp and 8pp from each live
        // dial to each other - every "this signal matters more than that one"
        // hypothesis, both directions, two magnitudes.
        foreach (var from in live)
        {
            foreach (var to in live)
            {
                if (from == to) continue;
                foreach (var magnitude in new[] { 0.04m, 0.08m })
                {
                    var arr = (decimal[])baseArr.Clone();
                    var shift = Math.Min(magnitude, arr[from] - 0.02m);
                    if (shift <= 0m || arr[to] + shift > 0.45m) continue;
                    arr[from] -= shift;
                    arr[to] += shift;
                    candidates.Add(baseline with
                    {
                        Label = $"Shift {magnitude * 100:0}pp: {names[from]} → {names[to]}",
                        Weights = FromArray(arr),
                    });
                }
            }
        }

        // Optional trading-RULE search ("Search for optimal trading rules"):
        // grid variants of the exit/probation/position rules, one dial at a
        // time on the BASELINE weights - the same one-change-at-a-time
        // philosophy as the weight nudges, so a winning rule candidate is
        // attributable to that rule. Values equal to the current production
        // rule are skipped (they'd duplicate the baseline). A few combined
        // risk-posture bundles cover the interaction between stop, target and
        // hold length that single-dial variants can't see. All of these are
        // added BEFORE the random filler so the rules search never gets
        // crowded out by the candidate cap; each still faces the same
        // constraints, robust-score ranking and holdout validation as every
        // weight candidate.
        if (searchRules)
            candidates.AddRange(GenerateRuleCandidates(baseline, productionRules, accountTactics));

        // Fill the remainder to TargetCandidateCount with deterministic
        // pseudo-random LIVE mixes (fixed seed - repeated sweeps on the same
        // data give the same answer). Half also jitter the Buy threshold so
        // the search covers the joint weights-threshold space, not just the
        // simplex at one threshold.
        var rng = new Random(20260710);
        var k = 0;
        while (candidates.Count < TargetCandidateCount)
        {
            k++;
            var arr = (decimal[])baseArr.Clone();
            foreach (var i in live)
                arr[i] = Math.Max(0.02m, baseArr[i] + (decimal)(rng.NextDouble() - 0.5) * 0.30m);
            RenormaliseLive(arr, liveBudget, live);

            var jitterThreshold = k % 2 == 0;
            var nt = jitterThreshold
                ? Math.Clamp(Math.Round(baseline.BuyThreshold + (decimal)(rng.NextDouble() - 0.5) * 2.0m, 1), 3.0m, 9.0m)
                : baseline.BuyThreshold;

            candidates.Add(baseline with
            {
                Label = jitterThreshold ? $"Random mix {k} (threshold {nt:0.0})" : $"Random mix {k}",
                Weights = FromArray(arr),
                BuyThreshold = nt,
            });
        }

        return candidates;
    }

    // The trading-RULE + per-setup search candidates generated off a given base
    // candidate (its weights held fixed, one rule/setup dial changed at a time).
    // Phase 1 calls this with the production baseline. The greedy second pass
    // (RunSweepAsync) calls it again with the best WEIGHT mix as the base and a
    // label prefix, so the sweep can discover a combined best-weights + best-rule
    // winner - which the one-change-at-a-time first pass alone cannot. Values
    // equal to the base's production rules are skipped (they'd duplicate it).
    public static List<HistoricBacktestCandidate> GenerateRuleCandidates(
        HistoricBacktestCandidate baseCandidate,
        HistoricTradingRules? productionRules,
        IReadOnlyDictionary<SetupType, HistoricSetupTactics>? accountTactics,
        string labelPrefix = "")
    {
        var result = new List<HistoricBacktestCandidate>();
        // Each rule candidate builds a FRESH HistoricTradingRules, which would
        // drop the baseline's excluded-setups list (e.g. a live-disabled setup).
        // Carry it onto every candidate so the whole rule search stays inside the
        // account's tradeable book instead of quietly re-admitting a setup.
        var baseExcluded = baseCandidate.Rules?.ExcludedSetups;
        void AddRule(string label, HistoricTradingRules rules) =>
            result.Add(baseCandidate with { Label = labelPrefix + label, Rules = rules with { ExcludedSetups = baseExcluded } });

        foreach (var v in new[] { 5, 8, 12, 15, 20, 30 })
            if (v != productionRules?.MaxHoldDays)
                AddRule($"Max hold {v}d", new HistoricTradingRules(MaxHoldDays: v));

        foreach (var v in new[] { 0.03m, 0.05m, 0.07m, 0.10m })
            if (v != productionRules?.StopLossPct)
                AddRule($"Stop {v:P0}", new HistoricTradingRules(StopLossPct: v));

        foreach (var v in new[] { 0.10m, 0.15m, 0.20m, 0.30m })
            if (v != productionRules?.TargetPct)
                AddRule($"Target {v:P0}", new HistoricTradingRules(TargetPct: v));

        foreach (var (act, dist) in new[] { (0.03m, 0.02m), (0.05m, 0.03m), (0.08m, 0.04m), (0.10m, 0.05m) })
            if (act != productionRules?.TrailingActivationPct || dist != productionRules?.TrailingDistancePct)
                AddRule($"Trail +{act:P0}/{dist:P0}",
                    new HistoricTradingRules(TrailingActivationPct: act, TrailingDistancePct: dist));

        foreach (var v in new[] { 2, 3, 5 })
            if (v != productionRules?.MinHoldDays)
                AddRule($"Probation {v}d", new HistoricTradingRules(MinHoldDays: v));

        foreach (var v in new[] { 0.3m, 0.45m, 0.6m })
            if (v != productionRules?.MomentumHealthThreshold)
                AddRule($"Health floor {v:0.00}", new HistoricTradingRules(MomentumHealthThreshold: v));

        foreach (var v in new[] { 3, 5, 8 })
            if (v != productionRules?.MaxOpenPositions)
                AddRule($"Max positions {v}", new HistoricTradingRules(MaxOpenPositions: v));

        // Capital deployment: locked-capital reserve and flat position size.
        // Both feed the un-locked-share deployment cap the engine now enforces,
        // so they only matter in single-book (Default/Neutral) runs - under Mixed
        // the per-regime books own exposure. Only offered when the live book's
        // position size is known, and only values that keep the book VALID for
        // apply (position x maxPositions <= 1 - locked, mirroring the live invariant).
        if (productionRules is { PositionFraction: { } livePos, MaxOpenPositions: { } liveMax })
        {
            var deployed = livePos * liveMax;
            foreach (var locked in new[] { 0.40m, 0.55m, 0.70m, 0.80m })
                if (locked != productionRules.LockedCapitalPct && deployed <= 1m - locked)
                    AddRule($"Locked capital {locked:P0}", new HistoricTradingRules(LockedCapitalPct: locked));

            var lockedNow = productionRules.LockedCapitalPct ?? 0m;
            foreach (var pos in new[] { 0.05m, 0.08m, 0.10m, 0.12m })
                if (pos != livePos && pos * liveMax <= 1m - lockedNow)
                    AddRule($"Position size {pos:P0}", new HistoricTradingRules(PositionFraction: pos));
        }

        AddRule("Tight risk (stop 3%, target 10%, trail +3%/2%, hold 10d)",
            new HistoricTradingRules(MaxHoldDays: 10, StopLossPct: 0.03m, TargetPct: 0.10m,
                TrailingActivationPct: 0.03m, TrailingDistancePct: 0.02m));
        AddRule("Loose risk (stop 10%, target 30%, trail +10%/5%, hold 30d)",
            new HistoricTradingRules(MaxHoldDays: 30, StopLossPct: 0.10m, TargetPct: 0.30m,
                TrailingActivationPct: 0.10m, TrailingDistancePct: 0.05m));
        AddRule("Let winners run (target 30%, trail +5%/3%, hold 30d)",
            new HistoricTradingRules(MaxHoldDays: 30, TargetPct: 0.30m,
                TrailingActivationPct: 0.05m, TrailingDistancePct: 0.03m));

        // Per-setup TACTICS search (docs/setup-tactics-plan Phase 4): nudges ONE
        // setup's guide-hold / stop / target off its live value at a time, holding
        // its other tactics and every OTHER setup at baseline, so a winner is
        // attributable to that setup + dial. Bounded (~2-3 nudges per dial per
        // setup) to keep the candidate budget sane. Stop/target target the
        // stop-out-rate vs let-winners-run tradeoff a mean-reversion setup lives on.
        if (accountTactics is not null)
        {
            foreach (var (setup, tac) in accountTactics.OrderBy(kv => kv.Key.ToString()))
            {
                var s = setup.ToString();
                HistoricSetupTacticsOverride Ov(int guideHold, decimal stop, decimal target) =>
                    new(s, stop, target, guideHold, tac.TrailingActivationPct, tac.TrailingDistancePct);

                // Guide-hold: ×0.5/×1.5/×2.0, clamped, distinct (the top two can
                // clamp to the same value, which would duplicate a candidate).
                foreach (var gh in new[] { 0.5m, 1.5m, 2.0m }
                    .Select(m => Math.Clamp((int)Math.Round(tac.GuideHoldDays * m), 5, 30))
                    .Where(gh => gh != tac.GuideHoldDays).Distinct())
                    AddRule($"{s} guide-hold {gh}d", new HistoricTradingRules(SetupTactics: [Ov(gh, tac.StopLossPct, tac.TargetPct)]));

                // Stop: tighter/wider. Kept strictly below the setup's target so the
                // stop/target structure stays valid. A wider stop is the direct lever
                // against a high stop-out rate; a tighter one cuts the loss per stop.
                foreach (var stop in new[] { 0.6m, 1.6m }
                    .Select(m => Math.Round(Math.Clamp(tac.StopLossPct * m, CapitalRules.MinStopLossPct, CapitalRules.MaxStopLossPct), 2))
                    .Where(v => v != tac.StopLossPct && v < tac.TargetPct).Distinct())
                    AddRule($"{s} stop {stop:P0}", new HistoricTradingRules(SetupTactics: [Ov(tac.GuideHoldDays, stop, tac.TargetPct)]));

                // Target: faster (bank sooner) / further (let winners run). Kept
                // strictly above the setup's stop.
                foreach (var target in new[] { 0.7m, 1.5m }
                    .Select(m => Math.Round(Math.Clamp(tac.TargetPct * m, CapitalRules.MinTargetPct, CapitalRules.MaxTargetPct), 2))
                    .Where(v => v != tac.TargetPct && v > tac.StopLossPct).Distinct())
                    AddRule($"{s} target {target:P0}", new HistoricTradingRules(SetupTactics: [Ov(tac.GuideHoldDays, tac.StopLossPct, target)]));
            }
        }

        return result;
    }

    // A candidate whose live dials follow the given proportions of the live
    // budget, dead dials kept at baseline.
    private static HistoricBacktestCandidate MakeLiveMix(
        HistoricBacktestCandidate baseline, decimal[] baseArr, decimal liveBudget,
        string label, decimal[] liveProportions, int[] live)
    {
        var arr = (decimal[])baseArr.Clone();
        for (var k = 0; k < live.Length; k++)
            arr[live[k]] = liveProportions[k] * liveBudget;
        RenormaliseLive(arr, liveBudget, live); // absorb rounding so the sum is exact
        return baseline with { Label = label, Weights = FromArray(arr) };
    }

    // Split each symbol's bars by the train-cutoff date derived from the SPY
    // calendar. The holdout slice keeps enough leading history for indicator
    // warmup, but the warmup bars overlap the train window by design - only
    // the trades generated after warmup land in the held-out evaluation.
    public static (Dictionary<string, DailyBar[]> Train, Dictionary<string, DailyBar[]> Holdout) SplitBars(
        IReadOnlyDictionary<string, DailyBar[]> bars, int warmupBars)
    {
        var spy = bars["SPY"];
        var cutoffIndex = (int)(spy.Length * TrainFraction);
        var cutoffDate = spy[cutoffIndex].Date;
        var holdoutStartDate = spy[Math.Max(0, cutoffIndex - warmupBars)].Date;

        var train = new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase);
        var holdout = new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, series) in bars)
        {
            var t = series.Where(b => b.Date < cutoffDate).ToArray();
            var h = series.Where(b => b.Date >= holdoutStartDate).ToArray();
            if (t.Length > 0) train[symbol] = t;
            if (h.Length > 0) holdout[symbol] = h;
        }
        return (train, holdout);
    }

    // Market-adjusted expectancy: for each closed trade, subtract SPY's return
    // over the same holding period, then average. This stops the objective
    // rewarding configs merely for being long during a rising market.
    public static decimal AdjustedExpectancy(HistoricResult result, DailyBar[] spy) =>
        AdjustedExpectancy(result.TradeLog, spy);

    // Split-half consistency check: the same adjusted expectancy computed
    // separately over the earlier and later halves of the result's own
    // window (trades split by entry date at the calendar midpoint). Costs
    // nothing - it re-reads the trade log the backtest already produced.
    // A candidate that made all its money in one stretch of the tuning
    // window scores well overall but poorly on its weak half, and that
    // lucky-in-one-regime profile is exactly what tends to collapse
    // out-of-sample - so the ML search ranks on the WORSE half (see
    // MlSweepOptimizer) while the holdout stays untouched for the final
    // verdict.
    public static (decimal Early, decimal Late) SplitHalfAdjustedExpectancy(HistoricResult result, DailyBar[] spy)
    {
        var midpoint = result.From + (result.To - result.From) / 2;
        return (
            AdjustedExpectancy(result.TradeLog.Where(t => t.EntryDate < midpoint), spy),
            AdjustedExpectancy(result.TradeLog.Where(t => t.EntryDate >= midpoint), spy));
    }

    // The split-half check with the LCB discount applied to each half - the
    // ML search's actual fitness combines both suspicions in one number:
    // "your worse stretch of the window, discounted for how few trades that
    // judgement rests on".
    public static (decimal Early, decimal Late) SplitHalfLowerBoundExpectancy(HistoricResult result, DailyBar[] spy)
    {
        var midpoint = result.From + (result.To - result.From) / 2;
        return (
            LowerBoundExpectancy(AdjustedTradeReturns(result.TradeLog.Where(t => t.EntryDate < midpoint), spy)),
            LowerBoundExpectancy(AdjustedTradeReturns(result.TradeLog.Where(t => t.EntryDate >= midpoint), spy)));
    }

    // Mean minus LcbStdErrMultiplier standard errors: the same average, but
    // discounted by its own sampling uncertainty. A 1.6% mean over 40 trades
    // (huge SE) can rank below a 0.8% mean over 190 trades (tiny SE), which
    // is exactly the ordering a raw mean gets wrong.
    public static decimal LowerBoundExpectancy(IReadOnlyList<decimal> returns)
    {
        if (returns.Count == 0) return 0m;
        var mean = returns.Average();
        if (returns.Count < 2) return Math.Round(mean, 2); // no variance estimate - the trade floors handle tiny samples
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var standardError = (decimal)Math.Sqrt((double)variance / returns.Count);
        return Math.Round(mean - LcbStdErrMultiplier * standardError, 2);
    }

    private static decimal AdjustedExpectancy(IEnumerable<HistoricTrade> trades, DailyBar[] spy)
    {
        var returns = AdjustedTradeReturns(trades, spy);
        return returns.Count > 0 ? Math.Round(returns.Average(), 2) : 0m;
    }

    private static List<decimal> AdjustedTradeReturns(IEnumerable<HistoricTrade> trades, DailyBar[] spy)
    {
        var spyByDate = spy.ToDictionary(b => b.Date, b => b.Close);

        var returns = new List<decimal>();
        foreach (var t in trades)
        {
            if (!spyByDate.TryGetValue(t.EntryDate, out var spyEntry) ||
                !spyByDate.TryGetValue(t.ExitDate, out var spyExit) || spyEntry <= 0)
            {
                returns.Add(t.ReturnPct); // no SPY bar to adjust against - use raw
                continue;
            }
            returns.Add(t.ReturnPct - (spyExit / spyEntry - 1m) * 100m);
        }
        return returns;
    }

    // baselineTrades enables the trade-rate floor: a candidate trading far
    // less than the baseline is a different (nearly inactive) strategy, not a
    // tuned version of it, and its high average is easy to hit by luck.
    // Passed as 0 (floor disabled) for the baseline candidate itself and
    // anywhere the reference count isn't known yet.
    // The effective train-trade floor: the fixed minimum, raised to a share
    // of the baseline's own trade count when that's known. Shared so the ML
    // search's infeasibility penalty slope points at the same threshold
    // Summarise rejects on.
    public static int MinTradesFor(int baselineTrades) =>
        baselineTrades > 0
            ? Math.Max(MinTrainTrades, (int)Math.Ceiling(baselineTrades * MinTradeRateVsBaselineFactor))
            : MinTrainTrades;

    public static SweepCandidateResult Summarise(
        HistoricBacktestCandidate candidate, HistoricResult result, DailyBar[] spy, decimal baselineMaxDrawdownPct,
        int baselineTrades = 0)
    {
        var adjusted = AdjustedExpectancy(result, spy);
        var minTrades = MinTradesFor(baselineTrades);

        string? rejection = null;
        if (result.Trades < minTrades)
            rejection = baselineTrades > 0 && minTrades > MinTrainTrades
                ? $"only {result.Trades} trades on the train window (minimum {minTrades}, ≥{MinTradeRateVsBaselineFactor:P0} of the baseline's {baselineTrades}) — too inactive to be a tuned version of the strategy"
                : $"only {result.Trades} trades on the train window (minimum {minTrades}) — too few to trust";
        else if (baselineMaxDrawdownPct > 0 && result.MaxDrawdownPct > baselineMaxDrawdownPct * MaxDrawdownCeilingFactor)
            rejection = $"max drawdown {result.MaxDrawdownPct:F1}% exceeds the ceiling ({baselineMaxDrawdownPct * MaxDrawdownCeilingFactor:F1}%, 1.25× baseline)";

        var (earlyLcb, lateLcb) = SplitHalfLowerBoundExpectancy(result, spy);

        return new SweepCandidateResult(
            candidate.Label, candidate.Weights, candidate.BuyThreshold, candidate.ExcludeBreakout, candidate.AutopauseDuringBear,
            result.Trades, result.WinRate, result.ExpectancyPct, adjusted,
            result.ProfitFactor, result.MaxDrawdownPct, result.TotalReturnPct,
            rejection is null, rejection,
            RobustScorePct: Math.Min(earlyLcb, lateLcb),
            Rules: candidate.Rules,
            ExcludedSetups: candidate.Rules?.ExcludedSetups
                ?? (candidate.ExcludeBreakout ? ["Breakout"] : []));
    }

    public static SweepValidation BuildValidation(
        HistoricResult winnerTrain, HistoricResult winnerHoldout, HistoricResult baselineHoldout,
        DailyBar[] trainSpy, DailyBar[] holdoutSpy)
    {
        var trainAdj = AdjustedExpectancy(winnerTrain, trainSpy);
        var holdoutAdj = AdjustedExpectancy(winnerHoldout, holdoutSpy);
        var baselineAdj = AdjustedExpectancy(baselineHoldout, holdoutSpy);

        // Three bars to clear: keep at least half the tuning-window edge on
        // held-out data, beat the baseline on that same held-out data, and be
        // POSITIVE outright - "less bad than production" is not a config worth
        // recommending, however well it retains its (negative) train number.
        var heldUp = holdoutAdj > 0m
            && holdoutAdj >= trainAdj * HoldupRetentionFactor
            && holdoutAdj >= baselineAdj;

        var verdict = heldUp
            ? $"Held up out-of-sample: the winning dials kept a market-adjusted expectancy of {holdoutAdj:F2}%/trade " +
              $"on data they were never tuned on (vs {trainAdj:F2}% on the tuning window, and vs the current production " +
              $"dials' {baselineAdj:F2}% on the same held-out period). That's the strongest evidence a backtest can give — " +
              "but it is still one historical period, not a guarantee."
            : $"Did NOT hold up out-of-sample: on data the dials were never tuned on, market-adjusted expectancy was " +
              $"{holdoutAdj:F2}%/trade (vs {trainAdj:F2}% on the tuning window; current production dials scored {baselineAdj:F2}% " +
              "on the same held-out period). A config that only wins on the data it was tuned on is most likely noise — " +
              "applying it is not recommended.";

        return new SweepValidation(
            winnerTrain with { TradeLog = [] },
            winnerHoldout with { TradeLog = [] },
            trainAdj, holdoutAdj,
            baselineHoldout with { TradeLog = [] },
            baselineAdj, heldUp, verdict);
    }

    internal static decimal[] ToArray(HistoricBacktestWeights w) =>
        [w.Rsi, w.Macd, w.Volume, w.SetupQuality, w.RelativeStrength, w.PriceLevel];

    internal static HistoricBacktestWeights FromArray(decimal[] a) =>
        new(a[0], a[1], a[2], a[3], a[4], a[5]);

    // Scales only the live components so they sum to the fixed live budget
    // (total minus the untouched dead weights) - the overall sum stays exactly
    // 1.0 without moving any dead dial.
    internal static void RenormaliseLive(decimal[] arr, decimal liveBudget) =>
        RenormaliseLive(arr, liveBudget, LiveIndices);

    internal static void RenormaliseLive(decimal[] arr, decimal liveBudget, int[] live)
    {
        if (live.Length == 0) return;
        var liveSum = live.Sum(i => arr[i]);
        if (liveSum <= 0m) return;
        foreach (var i in live) arr[i] = Math.Round(arr[i] / liveSum * liveBudget, 4);
        // Rounding drift lands on the largest live component so the sum is exact.
        var drift = liveBudget - live.Sum(i => arr[i]);
        var maxIdx = live.OrderByDescending(i => arr[i]).First();
        arr[maxIdx] += drift;
    }
}
