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
        new(0.17m, 0.09m, 0.21m, 0.16m, 0.12m, 0.10m, 0.05m, 0.10m);

    private static HistoricBacktestCandidate Baseline() =>
        new("Production baseline", ProdWeights, 6.0m, true);

    private static decimal Sum(HistoricBacktestWeights w) =>
        w.Rsi + w.Macd + w.Volume + w.Sentiment + w.SetupQuality + w.RelativeStrength + w.PriceLevel + w.FundamentalMomentum;

    private static HistoricResult Result(
        int trades, decimal expectancy, decimal maxDd, List<HistoricTrade>? log = null) =>
        new(new DateTime(2024, 1, 1), new DateTime(2025, 1, 1),
            trades, 0.5m, 5m, -5m, expectancy, 1.1m, 10m, maxDd, 20m,
            [], [], [], log ?? []);

    [Fact]
    public void GenerateCandidates_AllWeightSetsSumToOne_AndDeterministic()
    {
        var first = SweepOptimizer.GenerateCandidates(Baseline());
        var second = SweepOptimizer.GenerateCandidates(Baseline());

        first.Count.Should().BeGreaterThan(15).And.BeLessThan(35);
        first.Should().AllSatisfy(c => Sum(c.Weights).Should().BeApproximately(1.0m, 0.005m));
        first.Select(c => c.Label).Should().Equal(second.Select(c => c.Label));
        first.Select(c => c.Weights).Should().Equal(second.Select(c => c.Weights));
        first[0].Label.Should().Be("Production baseline");
    }

    [Fact]
    public void GenerateCandidates_NeverMovesDeadComponentWeights()
    {
        // The historic engine scores Sentiment/RelStrength/PriceLevel/
        // FundamentalMomentum at a fixed neutral 0.5, so shifting weight
        // into/out of them only dilutes conviction toward the midpoint - a
        // candidate could win for reasons that don't transfer to production.
        // Every candidate must therefore hold the dead four at baseline.
        var candidates = SweepOptimizer.GenerateCandidates(Baseline());

        candidates.Should().AllSatisfy(c =>
        {
            c.Weights.Sentiment.Should().Be(ProdWeights.Sentiment);
            c.Weights.RelativeStrength.Should().Be(ProdWeights.RelativeStrength);
            c.Weights.PriceLevel.Should().Be(ProdWeights.PriceLevel);
            c.Weights.FundamentalMomentum.Should().Be(ProdWeights.FundamentalMomentum);
        });

        // And the live dials must actually vary across candidates - the sweep
        // still needs something real to search over.
        candidates.Select(c => (c.Weights.Rsi, c.Weights.Macd, c.Weights.Volume, c.Weights.SetupQuality))
            .Distinct().Count().Should().BeGreaterThan(10);
    }

    [Fact]
    public void GenerateCandidates_IncludesBearAutopauseToggle()
    {
        // The regime filter IS reconstructable from bars (unlike the dead
        // components), so the sweep tests it flipped relative to the baseline.
        var candidates = SweepOptimizer.GenerateCandidates(Baseline()); // baseline default: autopause ON

        var toggle = candidates.Single(c => c.Label == "Bear autopause OFF");
        toggle.AutopauseDuringBear.Should().BeFalse();
        toggle.Weights.Should().Be(ProdWeights); // toggle-only candidate, weights untouched
        candidates.Count(c => !c.AutopauseDuringBear).Should().Be(1);
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
}

// Claude response parsing for the Lab analysis: strict-JSON happy path,
// fenced/prefixed JSON, invalid-weight rejection, and prose fallback.
public class LabAnalysisPromptsTests
{
    [Fact]
    public void ParseResponse_ValidJson_ReturnsAnalysisAndSuggestion()
    {
        const string raw = """
            {"analysis": "The stop-loss bucket dominates.", "suggestion": {"rsi": 0.17, "macd": 0.09,
             "volume": 0.26, "sentiment": 0.11, "setupQuality": 0.12, "relativeStrength": 0.10,
             "priceLevel": 0.05, "fundamentalMomentum": 0.10, "buyThreshold": 6.5,
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
}
