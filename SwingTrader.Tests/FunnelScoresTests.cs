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
}
