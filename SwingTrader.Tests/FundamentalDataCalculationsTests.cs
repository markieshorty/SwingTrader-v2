using FluentAssertions;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Fundamental;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using Xunit;

namespace SwingTrader.Tests;

// The pure classification pieces behind the FundamentalMomentum sub-scores:
// analyst revision VELOCITY, MSPR-adjusted insider activity, and earnings
// surprise ACCELERATION - all deterministic given their inputs.
public class FundamentalDataCalculationsTests
{
    // ── Analyst revision velocity ────────────────────────────────────────────

    [Fact]
    public void ClassifyAnalystTrend_VelocityDrivesTheTiers_NotTheLevel()
    {
        // Big improvement from a negative base is Bullish even though the
        // level is still unimpressive - the CHANGE is the signal.
        FundamentalDataService.ClassifyAnalystTrend(-0.4m, -0.25m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.Bullish);

        // A very bullish but FLAT consensus is Neutral: the level already
        // happened, and analysts chasing a move add nothing forward-looking.
        FundamentalDataService.ClassifyAnalystTrend(0.6m, 0.62m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.Neutral);
    }

    [Fact]
    public void ClassifyAnalystTrend_StrongTiers_NeedStrongVelocityOrVelocityPlusLevel()
    {
        // Velocity alone clears the strong bar.
        FundamentalDataService.ClassifyAnalystTrend(0.0m, 0.3m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.StronglyBullish);

        // Moderate velocity + already-positive level also qualifies.
        FundamentalDataService.ClassifyAnalystTrend(0.30m, 0.45m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.StronglyBullish);

        // Same moderate velocity from a weak level stays plain Bullish.
        FundamentalDataService.ClassifyAnalystTrend(0.0m, 0.15m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.Bullish);
    }

    [Fact]
    public void ClassifyAnalystTrend_BearishMirrors()
    {
        FundamentalDataService.ClassifyAnalystTrend(0.1m, -0.2m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.StronglyBearish);
        FundamentalDataService.ClassifyAnalystTrend(0.1m, -0.05m, 0.10m, 0.25m)
            .Should().Be(AnalystTrend.Bearish);
    }

    // ── MSPR-adjusted insider activity ───────────────────────────────────────

    [Theory]
    [InlineData(InsiderActivity.Buying, 30, InsiderActivity.StrongBuying)]      // agreeing MSPR upgrades
    [InlineData(InsiderActivity.Neutral, 30, InsiderActivity.Buying)]
    [InlineData(InsiderActivity.ClusterSelling, 30, InsiderActivity.Neutral)]   // conflicting reads cancel
    [InlineData(InsiderActivity.StrongBuying, -30, InsiderActivity.Buying)]     // disagreeing MSPR downgrades
    [InlineData(InsiderActivity.Neutral, -30, InsiderActivity.ClusterSelling)]
    [InlineData(InsiderActivity.Buying, 10, InsiderActivity.Buying)]            // between thresholds: no change
    public void CombineWithMspr_UpgradesDowngradesOneNotch(
        InsiderActivity clustering, int mspr, InsiderActivity expected) =>
        FundamentalDataService.CombineWithMspr(clustering, mspr, 20m, -20m).Should().Be(expected);

    [Fact]
    public void CombineWithMspr_Unavailable_LeavesClusteringVerdictAlone()
    {
        FundamentalDataService.CombineWithMspr(InsiderActivity.Buying, null, 20m, -20m)
            .Should().Be(InsiderActivity.Buying);
    }

    // ── Earnings surprise acceleration ───────────────────────────────────────

    private static FinnhubEarningsEvent Quarter(decimal? surprise) =>
        new("TEST", "2026-01-01", null, 1.0m, 1.1m, surprise);

    [Fact]
    public void ComputeSurpriseTrend_AcceleratingBeats_ArePositive()
    {
        // Newest first: 8%, 6% recent vs 3%, 1% older -> +5pp trend.
        var trend = FundamentalDataService.ComputeSurpriseTrend(
            [Quarter(8m), Quarter(6m), Quarter(3m), Quarter(1m)]);

        trend.Should().Be(5.0m);
    }

    [Fact]
    public void ComputeSurpriseTrend_ShrinkingBeats_AreNegative()
    {
        var trend = FundamentalDataService.ComputeSurpriseTrend(
            [Quarter(1m), Quarter(2m), Quarter(6m), Quarter(9m)]);

        trend.Should().Be(-6.0m);
    }

    [Fact]
    public void ComputeSurpriseTrend_UnderThreeUsableQuarters_ReturnsNull()
    {
        FundamentalDataService.ComputeSurpriseTrend([Quarter(5m), Quarter(3m)]).Should().BeNull();
        FundamentalDataService.ComputeSurpriseTrend([Quarter(5m), Quarter(null), Quarter(null)]).Should().BeNull();
    }

    [Fact]
    public void GetEarningsConsistency_ReturnsTierAndTrendTogether()
    {
        var cfg = new FundamentalConfig();
        var history = new List<FinnhubEarningsEvent>
        {
            Quarter(9m), Quarter(7m), Quarter(2m), Quarter(1m), // 4 beats, accelerating
        };

        var (consistency, trend) = FundamentalDataService.GetEarningsConsistency(history, cfg);

        consistency.Should().Be(EarningsConsistency.ConsistentBeater);
        trend.Should().Be(6.5m); // (9+7)/2 - (2+1)/2
    }
}
