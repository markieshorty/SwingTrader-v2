using FluentAssertions;
using SwingTrader.Agents.Research;
using SwingTrader.Agents.Report;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Phase F1 (docs/funnel-plan): the funnel's shadow scores. The Gate must be
// bit-identical to ConvictionScorer with the forward pair pinned neutral
// (that identity is the whole point - it's what HistoricBacktester models);
// the Forward blend must degrade honestly on missing inputs.
public class FunnelScoresTests
{
    private static readonly StrategyWeights Weights = new(); // production defaults, sum = 1.0

    [Fact]
    public void Gate_IsBitIdenticalToConvictionScorerWithNeutralForwardPair()
    {
        // Across a spread of component values, Gate(...) must equal the full
        // Calculate(...) call with sentiment/fundamental at exactly 0.5 -
        // pinning, not renormalising, is the locked design decision.
        foreach (var v in new[] { 0.0m, 0.25m, 0.5m, 0.75m, 1.0m })
        {
            var gate = FunnelScores.Gate(Weights, v, v, v, v, v, v);
            var full = ConvictionScorer.Calculate(Weights, v, v, v, 0.5m, v, v, v, 0.5m);
            gate.Should().Be(full);
        }
    }

    [Fact]
    public void Gate_IgnoresRealSentimentAndFundamentalEntirely()
    {
        // The same technical inputs must produce the same gate whatever the
        // forward pair would have said - that separation IS the funnel.
        var gate = FunnelScores.Gate(Weights, 0.8m, 0.7m, 0.9m, 1.0m, 0.6m, 0.4m);
        var legacyEuphoric = ConvictionScorer.Calculate(Weights, 0.8m, 0.7m, 0.9m, 1.0m, 1.0m, 0.6m, 0.4m, 1.0m);
        var legacyDire = ConvictionScorer.Calculate(Weights, 0.8m, 0.7m, 0.9m, 0.0m, 1.0m, 0.6m, 0.4m, 0.0m);

        legacyEuphoric.Should().NotBe(legacyDire); // sanity: the pair matters to legacy
        gate.Should().BeInRange(legacyDire, legacyEuphoric); // and gate sits at their neutral midpoint
    }

    [Fact]
    public void Forward_BlendsAndRescalesToTen()
    {
        var result = FunnelScores.Forward(0.8m, 0.6m, sentimentWeight: 0.6m, fundamentalWeight: 0.4m);

        result.Score.Should().Be(7.2m); // (0.6*0.8 + 0.4*0.6) * 10
        result.Degraded.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0.6, 5.4)]  // sentiment missing -> 0.5 substituted, degraded
    [InlineData(0.8, null, 6.8)]  // fundamental missing
    [InlineData(null, null, 5.0)] // both missing -> pure neutral
    public void Forward_MissingInputs_SubstituteNeutralAndFlagDegraded(double? s, double? f, double expected)
    {
        var result = FunnelScores.Forward((decimal?)s, (decimal?)f, 0.6m, 0.4m);

        result.Score.Should().Be((decimal)expected);
        result.Degraded.Should().BeTrue();
    }

    [Fact]
    public void FunnelShadowStats_CountsDivergenceBothWays()
    {
        StockSignal Sig(string sym, Recommendation rec, decimal? gate, bool wouldPass, bool wouldVeto = false) =>
            new() { Symbol = sym, Recommendation = rec, GateScore = gate, WouldPassGate = wouldPass, WouldBeVetoed = wouldVeto };

        var all = new List<StockSignal>
        {
            Sig("AGREE1", Recommendation.Buy, 7m, wouldPass: true),      // both buy
            Sig("AGREE2", Recommendation.Hold, 4m, wouldPass: false),    // both no
            Sig("GATEONLY", Recommendation.Watch, 7m, wouldPass: true),  // gate-only (+)
            Sig("LEGACYONLY", Recommendation.Buy, 5m, wouldPass: false), // legacy-only (-)
            Sig("VETOED", Recommendation.Watch, 3m, wouldPass: false, wouldVeto: true),
            Sig("PREFUNNEL", Recommendation.Buy, null, wouldPass: false), // scored before F1 - excluded
        };

        var stats = ReportGenerationService.ComputeFunnelShadowStats(all);

        stats.Scored.Should().Be(5);
        stats.LegacyBuys.Should().Be(2);
        stats.GateWouldBuy.Should().Be(2);
        stats.DivergentSymbols.Should().Equal("GATEONLY+", "LEGACYONLY-");
        stats.WouldVeto.Should().Be(1);
    }

    // ── Phase F3: the veto predicate + counterfactual scorecard ─────────────

    [Theory]
    [InlineData(2.4, false, 2.5, true)]   // just below the floor -> veto
    [InlineData(2.5, false, 2.5, false)]  // AT the floor -> no veto (strict <)
    [InlineData(2.6, false, 2.5, false)]  // above -> no veto
    [InlineData(0.0, true, 2.5, false)]   // degraded never vetoes, however low
    [InlineData(null, false, 2.5, false)] // missing never vetoes
    [InlineData(0.0, false, 0.0, false)]  // floor 0 = veto off (scores are >= 0)
    public void ShouldVeto_FiresOnlyForRealScoresStrictlyBelowTheFloor(
        double? forward, bool degraded, double floor, bool expected) =>
        FunnelScores.ShouldVeto((decimal?)forward, degraded, (decimal)floor)
            .Should().Be(expected);

    [Fact]
    public void ForwardVetoFloor_OutsideTheRail_FailsValidation()
    {
        var profile = new AccountRiskProfile { ForwardVetoFloor = 5.1m };

        var act = profile.Validate;

        act.Should().Throw<System.ComponentModel.DataAnnotations.ValidationException>()
            .WithMessage("*veto floor*");
    }

    [Fact]
    public void ComputeVetoScorecard_PricesCounterfactualsFromTheSymbolsOwnLaterSignals()
    {
        var today = new DateOnly(2026, 7, 12);
        StockSignal Sig(string sym, int daysAgo, decimal price, bool vetoed = false) => new()
        {
            Symbol = sym, SignalDate = today.AddDays(-daysAgo), CurrentPrice = price,
            WouldPassGate = vetoed, WouldBeVetoed = vetoed,
        };

        var window = new List<StockSignal>
        {
            Sig("UP", 10, 100m, vetoed: true),   // vetoed at 100...
            Sig("UP", 0, 110m),                  // ...now 110 -> +10%
            Sig("DOWN", 10, 100m, vetoed: true), // vetoed at 100...
            Sig("DOWN", 0, 90m),                 // ...now 90 -> -10%
            Sig("GONE", 10, 50m, vetoed: true),  // dropped off watchlist - counted, not measurable
            Sig("TODAY", 0, 80m, vetoed: true),  // vetoed today - no elapsed time, excluded entirely
        };
        var closed = new List<Trade>
        {
            new() { Symbol = "X", EntryPrice = 100m, ExitPrice = 105m }, // +5%
            new() { Symbol = "Y", EntryPrice = 100m, ExitPrice = 99m },  // -1%
            new() { Symbol = "Z", EntryPrice = 100m, ExitPrice = null }, // no fill price - excluded
        };

        var card = ReportGenerationService.ComputeVetoScorecard(today, window, closed);

        card.VetoedInWindow.Should().Be(3);       // UP, DOWN, GONE (not TODAY)
        card.MeasurableVetoes.Should().Be(2);     // GONE has no later price
        card.AvgVetoCounterfactualPct.Should().Be(0m); // (+10 - 10) / 2
        card.ClosedBuys.Should().Be(2);
        card.AvgClosedBuyReturnPct.Should().Be(2m);    // (+5 - 1) / 2
    }

    [Fact]
    public void ComputeVetoScorecard_EmptyWindow_ReportsZeroesAndNulls()
    {
        var card = ReportGenerationService.ComputeVetoScorecard(
            new DateOnly(2026, 7, 12), [], []);

        card.VetoedInWindow.Should().Be(0);
        card.AvgVetoCounterfactualPct.Should().BeNull();
        card.AvgClosedBuyReturnPct.Should().BeNull();
    }
}
