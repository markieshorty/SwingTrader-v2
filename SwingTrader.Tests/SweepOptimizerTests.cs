using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The optimizer sweep's pure pieces: candidate generation (deterministic,
// weights always sum to 1), the train/holdout split, the market-adjusted
// expectancy objective, the eligibility guardrails, and the out-of-sample
// hold-up verdict.
public class SweepOptimizerTests
{
    private static readonly HistoricBacktestWeights ProdWeights =
        new(0.23m, 0.12m, 0.28m, 0.16m, 0.14m, 0.07m);

    private static HistoricBacktestCandidate Baseline() =>
        new("Production baseline", ProdWeights, 6.0m, true);

    private static decimal Sum(HistoricBacktestWeights w) =>
        w.Rsi + w.Macd + w.Volume + w.SetupQuality + w.RelativeStrength + w.PriceLevel;

    private static HistoricResult Result(
        int trades, decimal expectancy, decimal maxDd, List<HistoricTrade>? log = null) =>
        new(new DateTime(2024, 1, 1), new DateTime(2025, 1, 1),
            trades, 0.5m, 5m, -5m, expectancy, 1.1m, 10m, maxDd, 20m,
            [], [], [], log ?? []);

    [Fact]
    public void GenerateCandidates_ExcludedSetupsPropagateToEveryCandidate()
    {
        // A baseline that excludes a live-disabled setup: the whole search must
        // stay inside that book - both weight candidates (which keep Rules via
        // `with`) and rule candidates (which build a fresh Rules).
        var baseline = Baseline() with { Rules = new HistoricTradingRules(ExcludedSetups: ["Breakout"]) };

        var candidates = SweepOptimizer.GenerateCandidates(baseline, searchRules: true);

        candidates.Should().OnlyContain(c =>
            c.Rules != null && c.Rules.ExcludedSetups != null && c.Rules.ExcludedSetups.Contains("Breakout"));
        // Sanity: the rule search actually produced rule candidates (fresh Rules).
        candidates.Should().Contain(c => c.Label.Contains("Stop "));
    }

    [Fact]
    public void GenerateCandidates_AllWeightSetsSumToOne_AndDeterministic()
    {
        var first = SweepOptimizer.GenerateCandidates(Baseline());
        var second = SweepOptimizer.GenerateCandidates(Baseline());

        first.Count.Should().Be(SweepOptimizer.TargetCandidateCount);
        first.Should().AllSatisfy(c => Sum(c.Weights).Should().BeApproximately(1.0m, 0.005m));
        first.Select(c => c.Label).Should().Equal(second.Select(c => c.Label));
        first.Select(c => c.Weights).Should().Equal(second.Select(c => c.Weights));
        first[0].Label.Should().Be("Production baseline");
    }

    [Fact]
    public void GenerateCandidates_VariesAllSixGateWeights()
    {
        // All six gate weights are searched now (sentiment/fundamental are no
        // longer part of the gate).
        var candidates = SweepOptimizer.GenerateCandidates(Baseline());

        candidates.Select(c => (c.Weights.Rsi, c.Weights.Macd, c.Weights.Volume,
                c.Weights.SetupQuality, c.Weights.RelativeStrength, c.Weights.PriceLevel))
            .Distinct().Count().Should().BeGreaterThan(10);
    }

    [Fact]
    public void GenerateRuleCandidates_SearchesLockedCapitalAndPositionSize_WithinValidBounds()
    {
        // Live book: 10% position x 5 max = 50% deployed, 55% locked. The sweep
        // offers locked/position grid points that keep the book valid for apply
        // (position x maxPositions <= 1 - locked).
        var prod = new HistoricTradingRules(
            PositionFraction: 0.10m, MaxOpenPositions: 5, LockedCapitalPct: 0.55m);

        var candidates = SweepOptimizer.GenerateCandidates(Baseline(), searchRules: true, productionRules: prod);

        candidates.Should().Contain(c => c.Label.StartsWith("Locked capital"));
        candidates.Should().Contain(c => c.Label.StartsWith("Position size"));
        // Never offers a locked value that would over-deploy the un-locked share.
        candidates.Where(c => c.Label.StartsWith("Locked capital"))
            .Should().OnlyContain(c => 0.10m * 5 <= 1m - c.Rules!.LockedCapitalPct!.Value);
    }

    [Fact]
    public void GenerateCandidates_NoLongerSweepsAutopause()
    {
        // Autopause is now a per-regime-book decision (Risk Management / the
        // Regimes comparison), not a single-book weight lever - the old SPY-200
        // proxy toggle is retired, so no candidate flips it off the baseline.
        var candidates = SweepOptimizer.GenerateCandidates(Baseline()); // baseline default: autopause ON

        candidates.Should().OnlyContain(c => c.AutopauseDuringBear == Baseline().AutopauseDuringBear);
        candidates.Should().NotContain(c => c.Label.Contains("autopause"));
    }

    [Fact]
    public void GenerateCandidates_PerSetupGuideHoldSearch_OnlyWhenTacticsProvidedAndSearchRules()
    {
        var tactics = new Dictionary<SwingTrader.Core.Enums.SetupType, HistoricSetupTactics>
        {
            [SwingTrader.Core.Enums.SetupType.Breakout] =
                new(0.05m, 0.08m, GuideHoldDays: 10, 0.05m, 0.03m),
        };

        // No per-setup candidates without the tactics map...
        var noTactics = SweepOptimizer.GenerateCandidates(Baseline(), searchRules: true);
        noTactics.Should().NotContain(c => c.Label!.StartsWith("Breakout guide-hold"));

        // ...nor when searchRules is off...
        var noSearch = SweepOptimizer.GenerateCandidates(Baseline(), searchRules: false, accountTactics: tactics);
        noSearch.Should().NotContain(c => c.Label!.StartsWith("Breakout guide-hold"));

        // ...but present, bounded and carrying a per-setup override when both on.
        var withSearch = SweepOptimizer.GenerateCandidates(Baseline(), searchRules: true, accountTactics: tactics);
        var perSetup = withSearch.Where(c => c.Label!.StartsWith("Breakout guide-hold")).ToList();
        perSetup.Should().NotBeEmpty();
        perSetup.Count.Should().BeLessThanOrEqualTo(3); // guide-hold dimension only, ≤3 nudges/setup
        perSetup.Should().OnlyContain(c =>
            c.Rules != null && c.Rules.SetupTactics != null && c.Rules.SetupTactics.Count == 1
            && c.Rules.SetupTactics[0].Setup == "Breakout"
            && c.Rules.SetupTactics[0].GuideHoldDays != 10);

        // The per-setup search also nudges stop and target (one dial at a time).
        var stopCands = withSearch.Where(c => c.Label!.StartsWith("Breakout stop")).ToList();
        var targetCands = withSearch.Where(c => c.Label!.StartsWith("Breakout target")).ToList();
        stopCands.Should().NotBeEmpty();
        targetCands.Should().NotBeEmpty();
        // A stop nudge moves only the stop (target/guide-hold stay at baseline)
        // and stays strictly below the target so the structure is valid.
        stopCands.Should().OnlyContain(c =>
            c.Rules!.SetupTactics![0].StopLossPct != 0.05m
            && c.Rules.SetupTactics[0].TargetPct == 0.08m
            && c.Rules.SetupTactics[0].GuideHoldDays == 10
            && c.Rules.SetupTactics[0].StopLossPct < c.Rules.SetupTactics[0].TargetPct);
        targetCands.Should().OnlyContain(c =>
            c.Rules!.SetupTactics![0].TargetPct != 0.08m
            && c.Rules.SetupTactics[0].StopLossPct == 0.05m
            && c.Rules.SetupTactics[0].TargetPct > c.Rules.SetupTactics[0].StopLossPct);
    }

    [Fact]
    public void GenerateRuleCandidates_UsesBaseWeightsAndPrefix_ForTheGreedySecondPass()
    {
        // The greedy pass fixes the best WEIGHT mix and searches rules on top.
        var tunedWeights = new HistoricBacktestWeights(0.30m, 0.08m, 0.10m, 0.30m, 0.08m, 0.14m);
        var refineBase = new HistoricBacktestCandidate("Tuned weights", tunedWeights, 6.0m, true);

        var ruleCandidates = SweepOptimizer.GenerateRuleCandidates(
            refineBase, productionRules: null, accountTactics: null, labelPrefix: "Tuned + ");

        ruleCandidates.Should().NotBeEmpty();
        // Every rule candidate keeps the tuned weights (only the rule changes)...
        ruleCandidates.Should().OnlyContain(c => c.Weights == tunedWeights && c.Rules != null);
        // ...and carries the prefix so it doesn't collide with the phase-1 label.
        // (Label uses "P0", whose percent glyph spacing is culture-dependent, so
        // match on the stem rather than the exact "5%" / "5 %" rendering.)
        ruleCandidates.Should().OnlyContain(c => c.Label!.StartsWith("Tuned + "));
        ruleCandidates.Should().Contain(c => c.Label!.StartsWith("Tuned + Stop "));
    }

    [Fact]
    public void SplitBars_TrainEndsBeforeHoldoutEvaluationStarts_WithWarmupOverlap()
    {
        var start = new DateTime(2023, 1, 2);
        var series = Enumerable.Range(0, 1000)
            .Select(i => new DailyBar(start.AddDays(i), 100m, 101m, 99m, 100m, 1_000_000m))
            .ToArray();
        var bars = new Dictionary<string, DailyBar[]> { ["SPY"] = series, ["AAPL"] = series };

        var (train, holdout) = SweepOptimizer.SplitBars(bars, warmupBars: 60);

        var cutoff = series[(int)(1000 * SweepOptimizer.TrainFraction)].Date;
        train["SPY"].Last().Date.Should().BeBefore(cutoff);
        // Holdout keeps 60 warmup bars before the cutoff so indicators can
        // initialize, but no more than that.
        holdout["SPY"].First().Date.Should().Be(series[(int)(1000 * SweepOptimizer.TrainFraction) - 60].Date);
        holdout["SPY"].Last().Date.Should().Be(series[^1].Date);
    }

    [Fact]
    public void AdjustedExpectancy_SubtractsSpyReturnOverSameHoldingPeriod()
    {
        var d0 = new DateTime(2024, 1, 1);
        var d1 = new DateTime(2024, 1, 10);
        var spy = new[]
        {
            new DailyBar(d0, 100m, 100m, 100m, 100m, 1m),
            new DailyBar(d1, 105m, 105m, 105m, 105m, 1m), // SPY +5% during the trade
        };
        // Trade made +8% raw while SPY made +5% -> +3% market-adjusted.
        var log = new List<HistoricTrade>
        {
            new("AAPL", d0, d1, 100m, 108m, Core.Enums.SetupType.MomentumContinuation, 6.5m, "Target", 8m),
        };

        SweepOptimizer.AdjustedExpectancy(Result(1, 8m, 5m, log), spy).Should().Be(3.00m);
    }

    [Fact]
    public void SplitHalfAdjustedExpectancy_SplitsTradesAtTheWindowMidpoint()
    {
        // Window is 2024-01-01 .. 2025-01-01 (Result helper), midpoint ~1 Jul.
        // Two +2% trades land in the early half, one -1% trade in the late
        // half; SPY is flat so raw returns pass through unadjusted. The ML
        // search ranks candidates on the WORSE half, so this lopsided log
        // must score -1, not the +1 the full-window average would show.
        var spy = new[] { new DailyBar(new DateTime(2024, 1, 1), 100m, 100m, 100m, 100m, 1m) };
        var log = new List<HistoricTrade>
        {
            new("A", new DateTime(2024, 2, 1), new DateTime(2024, 2, 10), 100m, 102m, Core.Enums.SetupType.MomentumContinuation, 6.5m, "Target", 2m),
            new("B", new DateTime(2024, 4, 1), new DateTime(2024, 4, 10), 100m, 102m, Core.Enums.SetupType.MomentumContinuation, 6.5m, "Target", 2m),
            new("C", new DateTime(2024, 10, 1), new DateTime(2024, 10, 10), 100m, 99m, Core.Enums.SetupType.MomentumContinuation, 6.5m, "StopLoss", -1m),
        };

        var (early, late) = SweepOptimizer.SplitHalfAdjustedExpectancy(Result(3, 1m, 5m, log), spy);

        early.Should().Be(2.00m);
        late.Should().Be(-1.00m);
    }

    [Fact]
    public void SplitHalfAdjustedExpectancy_EmptyHalfScoresZero()
    {
        // A candidate whose trades all bunch into one half gets 0 for the
        // empty half - min(expectancy, 0) then caps its consistency score,
        // which is the intended suspicion toward one-regime candidates.
        var spy = new[] { new DailyBar(new DateTime(2024, 1, 1), 100m, 100m, 100m, 100m, 1m) };
        var log = new List<HistoricTrade>
        {
            new("A", new DateTime(2024, 2, 1), new DateTime(2024, 2, 10), 100m, 103m, Core.Enums.SetupType.MomentumContinuation, 6.5m, "Target", 3m),
        };

        var (early, late) = SweepOptimizer.SplitHalfAdjustedExpectancy(Result(1, 3m, 5m, log), spy);

        early.Should().Be(3.00m);
        late.Should().Be(0m);
    }

    [Fact]
    public void Summarise_RejectsTooFewTrades_AndExcessiveDrawdown()
    {
        var spy = new[] { new DailyBar(new DateTime(2024, 1, 1), 100m, 100m, 100m, 100m, 1m) };

        var tooFew = SweepOptimizer.Summarise(Baseline(), Result(SweepOptimizer.MinTrainTrades - 1, 1m, 10m), spy, 10m);
        tooFew.MetConstraints.Should().BeFalse();
        tooFew.RejectionReason.Should().Contain("too few");

        var tooDeep = SweepOptimizer.Summarise(Baseline(), Result(100, 1m, 13m), spy, 10m);
        tooDeep.MetConstraints.Should().BeFalse();
        tooDeep.RejectionReason.Should().Contain("drawdown");

        var fine = SweepOptimizer.Summarise(Baseline(), Result(100, 1m, 12m), spy, 10m);
        fine.MetConstraints.Should().BeTrue();
        fine.RejectionReason.Should().BeNull();
    }

    [Fact]
    public void Summarise_TradeRateFloor_RejectsInactiveCandidateWhenBaselineTradesKnown()
    {
        var spy = new[] { new DailyBar(new DateTime(2024, 1, 1), 100m, 100m, 100m, 100m, 1m) };

        // Baseline took 200 trades -> floor is max(40, 100) = 100. A 60-trade
        // candidate clears the fixed 40-trade minimum but is far too inactive
        // relative to the baseline - it's a different strategy, not a tuned
        // one - so with baselineTrades known it's rejected...
        var inactive = SweepOptimizer.Summarise(Baseline(), Result(60, 1m, 5m), spy, 10m, baselineTrades: 200);
        inactive.MetConstraints.Should().BeFalse();
        inactive.RejectionReason.Should().Contain("inactive");

        // ...but the SAME candidate passes when the reference count isn't
        // known (baselineTrades: 0 disables the rate floor, fixed 40 minimum).
        var noFloor = SweepOptimizer.Summarise(Baseline(), Result(60, 1m, 5m), spy, 10m, baselineTrades: 0);
        noFloor.MetConstraints.Should().BeTrue();

        // A candidate trading at a comparable rate (>= 50% of baseline) passes.
        var active = SweepOptimizer.Summarise(Baseline(), Result(120, 1m, 5m), spy, 10m, baselineTrades: 200);
        active.MetConstraints.Should().BeTrue();
    }

    [Fact]
    public void MinTradesFor_RaisesFloorToHalfBaseline_ButNeverBelowTheFixedMinimum()
    {
        SweepOptimizer.MinTradesFor(0).Should().Be(SweepOptimizer.MinTrainTrades);   // unknown -> fixed floor
        SweepOptimizer.MinTradesFor(50).Should().Be(SweepOptimizer.MinTrainTrades);  // 25 < 40, floor wins
        SweepOptimizer.MinTradesFor(200).Should().Be(100);                            // 50% of 200
    }

    [Fact]
    public void LowerBoundExpectancy_DiscountsHighVarianceSmallSamples_BelowSteadyLargerOnes()
    {
        // A "lucky" sample: high mean, wild spread, few trades.
        var lucky = new[] { 20m, -15m, 22m, -10m, 25m }; // mean 8.4%, big SE
        // A "steady" sample: lower mean, tight spread, many trades.
        var steady = Enumerable.Repeat(1.2m, 60).ToList();

        var luckyLcb = SweepOptimizer.LowerBoundExpectancy(lucky);
        var steadyLcb = SweepOptimizer.LowerBoundExpectancy(steady);

        // On the raw mean the lucky sample wins (8.4 vs 1.2); after the
        // standard-error discount the steady one does - exactly the reversal
        // the ranking change exists to produce.
        lucky.Average().Should().BeGreaterThan(steady.Average());
        steadyLcb.Should().BeGreaterThan(luckyLcb);
        steadyLcb.Should().BeApproximately(1.2m, 0.01m); // zero variance -> LCB == mean
    }

    [Fact]
    public void BuildValidation_CollapsedHoldout_IsFlaggedNotRecommended()
    {
        var d = new DateTime(2024, 1, 2);
        var spy = new[] { new DailyBar(d, 100m, 100m, 100m, 100m, 1m) };
        HistoricTrade T(decimal ret) => new("X", d, d, 100m, 100m + ret, Core.Enums.SetupType.Unknown, 6m, "Target", ret);

        // Train: +2%/trade. Holdout: -1%/trade -> collapse.
        var winnerTrain = Result(50, 2m, 5m, [T(2m)]);
        var winnerHoldout = Result(20, -1m, 5m, [T(-1m)]);
        var baselineHoldout = Result(20, 0.5m, 5m, [T(0.5m)]);

        var v = SweepOptimizer.BuildValidation(winnerTrain, winnerHoldout, baselineHoldout, spy, spy);

        v.HeldUp.Should().BeFalse();
        v.Verdict.Should().Contain("NOT hold up").And.Contain("not recommended");
    }

    [Fact]
    public void BuildValidation_NegativeHoldoutExpectancy_NeverHeldUp_EvenIfItBeatsBaseline()
    {
        // "Less bad than production" is not a recommendation. A winner whose
        // held-out expectancy is negative must be flagged even when it retains
        // its train edge and beats the baseline on the same held-out window.
        var d = new DateTime(2024, 1, 2);
        var spy = new[] { new DailyBar(d, 100m, 100m, 100m, 100m, 1m) };
        HistoricTrade T(decimal ret) => new("X", d, d, 100m, 100m + ret, Core.Enums.SetupType.Unknown, 6m, "Target", ret);

        var winnerTrain = Result(50, -0.1m, 5m, [T(-0.1m)]);
        var winnerHoldout = Result(20, -0.04m, 5m, [T(-0.04m)]);   // retains edge, still negative
        var baselineHoldout = Result(20, -0.3m, 5m, [T(-0.3m)]);   // baseline even worse

        var v = SweepOptimizer.BuildValidation(winnerTrain, winnerHoldout, baselineHoldout, spy, spy);

        v.HeldUp.Should().BeFalse();
    }

    [Fact]
    public void BuildValidation_RetainedHoldout_BeatingBaseline_HoldsUp()
    {
        var d = new DateTime(2024, 1, 2);
        var spy = new[] { new DailyBar(d, 100m, 100m, 100m, 100m, 1m) };
        HistoricTrade T(decimal ret) => new("X", d, d, 100m, 100m + ret, Core.Enums.SetupType.Unknown, 6m, "Target", ret);

        var winnerTrain = Result(50, 2m, 5m, [T(2m)]);
        var winnerHoldout = Result(20, 1.5m, 5m, [T(1.5m)]);   // keeps 75% of train edge
        var baselineHoldout = Result(20, 0.5m, 5m, [T(0.5m)]); // and beats baseline

        var v = SweepOptimizer.BuildValidation(winnerTrain, winnerHoldout, baselineHoldout, spy, spy);

        v.HeldUp.Should().BeTrue();
        v.Verdict.Should().Contain("Held up");
    }

    // ── "Search for optimal trading rules" ──────────────────────────────────

    [Fact]
    public void GenerateCandidates_SearchRulesOff_EmitsNoRuleCandidates()
    {
        SweepOptimizer.GenerateCandidates(Baseline())
            .Should().AllSatisfy(c => c.Rules.Should().BeNull());
    }

    [Fact]
    public void GenerateCandidates_SearchRulesOn_EmitsRuleCandidates_OnBaselineWeights()
    {
        var candidates = SweepOptimizer.GenerateCandidates(Baseline(), searchRules: true);

        var ruleCandidates = candidates.Where(c => c.Rules is not null).ToList();
        ruleCandidates.Should().NotBeEmpty();
        // Rule candidates test the rule change in isolation: baseline weights
        // and threshold, one-dial (or one bundle) rule overrides.
        ruleCandidates.Should().AllSatisfy(c =>
        {
            c.Weights.Should().Be(ProdWeights);
            c.BuyThreshold.Should().Be(6.0m);
        });
        // Total stays capped - rule candidates displace random filler rather
        // than extending the run.
        candidates.Count.Should().Be(SweepOptimizer.TargetCandidateCount);
    }

    [Fact]
    public void GenerateCandidates_SearchRulesOn_SkipsGridPointsEqualToProduction()
    {
        var production = new HistoricTradingRules(
            MaxHoldDays: 15, StopLossPct: 0.05m, TargetPct: 0.20m,
            TrailingActivationPct: 0.05m, TrailingDistancePct: 0.03m,
            MinHoldDays: 3, MomentumHealthThreshold: 0.45m, MaxOpenPositions: 5);

        var candidates = SweepOptimizer.GenerateCandidates(Baseline(), searchRules: true, productionRules: production);

        // Grid points matching production would just duplicate the baseline run.
        candidates.Should().NotContain(c => c.Rules != null && c.Label.StartsWith("Max hold") && c.Rules.MaxHoldDays == 15);
        candidates.Should().NotContain(c => c.Rules != null && c.Label.StartsWith("Stop") && c.Rules.StopLossPct == 0.05m);
        candidates.Should().NotContain(c => c.Rules != null && c.Label.StartsWith("Probation") && c.Rules.MinHoldDays == 3);
        // Other grid points remain.
        candidates.Should().Contain(c => c.Rules != null && c.Label.StartsWith("Max hold") && c.Rules.MaxHoldDays == 30);
    }

    [Fact]
    public void Summarise_CarriesCandidateRulesOntoTheSummary()
    {
        var rules = new HistoricTradingRules(MaxHoldDays: 12);
        var candidate = Baseline() with { Label = "Max hold 12d", Rules = rules };
        var spy = new[] { new DailyBar(new DateTime(2024, 1, 1), 100m, 100m, 100m, 100m, 1m) };

        var summary = SweepOptimizer.Summarise(candidate, Result(100, 0.5m, 8m), spy, 10m);

        summary.Rules.Should().Be(rules);
    }
}

// Claude response parsing for the Lab analysis: strict-JSON happy path,
// fenced/prefixed JSON, invalid-weight rejection, and prose fallback.
public class LabAnalysisPromptsTests
{
    [Fact]
    public void ParseResponse_ValidJson_ReturnsAnalysisAndSuggestion()
    {
        const string raw = """
            {"analysis": "The stop-loss bucket dominates.", "suggestion": {"rsi": 0.17, "macd": 0.12,
             "volume": 0.26, "setupQuality": 0.19, "relativeStrength": 0.16,
             "priceLevel": 0.10, "buyThreshold": 6.5,
             "excludeBreakout": true, "rationale": "Tests whether volume confirmation filters stop-outs."}}
            """;

        var (analysis, suggestion) = LabAnalysisPrompts.ParseResponse(raw);

        analysis.Should().Be("The stop-loss bucket dominates.");
        suggestion.Should().NotBeNull();
        suggestion!.Weights.Volume.Should().Be(0.26m);
        suggestion.BuyThreshold.Should().Be(6.5m);
        suggestion.Rationale.Should().Contain("volume confirmation");
    }

    [Fact]
    public void ParseResponse_FencedJson_StillParses()
    {
        const string raw = "```json\n{\"analysis\": \"Fine.\", \"suggestion\": null}\n```";

        var (analysis, suggestion) = LabAnalysisPrompts.ParseResponse(raw);

        analysis.Should().Be("Fine.");
        suggestion.Should().BeNull();
    }

    [Fact]
    public void ParseResponse_SuggestionWeightsNotSummingToOne_DropsSuggestionKeepsAnalysis()
    {
        const string raw = """
            {"analysis": "Something.", "suggestion": {"rsi": 0.9, "macd": 0.9, "volume": 0.1,
             "sentiment": 0.1, "setupQuality": 0.1, "relativeStrength": 0.1, "priceLevel": 0.1,
             "fundamentalMomentum": 0.1, "buyThreshold": 6.0, "excludeBreakout": true, "rationale": "x"}}
            """;

        var (analysis, suggestion) = LabAnalysisPrompts.ParseResponse(raw);

        analysis.Should().Be("Something.");
        suggestion.Should().BeNull();
    }

    [Fact]
    public void ParseResponse_PlainProse_FallsBackToWholeTextAsAnalysis()
    {
        var (analysis, suggestion) = LabAnalysisPrompts.ParseResponse("Just words, no JSON at all.");

        analysis.Should().Be("Just words, no JSON at all.");
        suggestion.Should().BeNull();
    }

    [Fact]
    public void ParseResponse_FencedProse_StripsFenceFromAnalysis()
    {
        // A model that ignores the JSON instruction and fences prose (no braces)
        // must not leak the ```json / ``` markers into the displayed writeup.
        const string raw = "```json\nThe winner widened the stop; held up out-of-sample.\n```";

        var (analysis, suggestion) = LabAnalysisPrompts.ParseResponse(raw);

        analysis.Should().Be("The winner widened the stop; held up out-of-sample.");
        suggestion.Should().BeNull();
    }
}
