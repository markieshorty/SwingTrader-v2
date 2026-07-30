using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Fundamental;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;
using Xunit;

namespace SwingTrader.Tests;

public class FundamentalScoringServiceTests
{
    private readonly IClaudeClient _claude = Substitute.For<IClaudeClient>();
    private readonly FundamentalConfig _config = new()
    {
        AnalystSubWeight = 0.25m,
        InsiderSubWeight = 0.25m,
        EarningsSubWeight = 0.25m,
        RevenueSubWeight = 0.25m,
    };

    private FundamentalScoringService CreateSut() => new(Options.Create(_config));

    private static FundamentalSnapshot Snapshot(
        AnalystTrend analyst, InsiderActivity insider, EarningsConsistency earnings, RevenueDirection revenue) =>
        new("AAPL", analyst, insider, earnings, revenue, AnalystCount: 10, InsiderBuyerCount: 0, InsiderSellerCount: 0,
            NetInsiderShares: null, FetchedAt: DateTime.UtcNow);

    [Fact]
    public async Task ScoreAsync_BestCaseSnapshot_ScoresAtMaximum()
    {
        var snapshot = Snapshot(AnalystTrend.StronglyBullish, InsiderActivity.StrongBuying, EarningsConsistency.ConsistentBeater, RevenueDirection.Accelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(1.0m);
    }

    [Fact]
    public async Task ScoreAsync_WorstCaseSnapshot_ScoresNearMinimum()
    {
        var snapshot = Snapshot(AnalystTrend.StronglyBearish, InsiderActivity.ClusterSelling, EarningsConsistency.ConsistentMisser, RevenueDirection.Decelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        // ClusterSelling's sub-score is 0.15 (not 0), so the worst case isn't
        // a flat zero - it should still land well below the 0.5 midpoint.
        result.Score.Should().BeLessThan(0.1m);
    }

    [Fact]
    public async Task ScoreAsync_AllNeutralInputs_ScoresAtMidpoint()
    {
        var snapshot = Snapshot(AnalystTrend.Neutral, InsiderActivity.Neutral, EarningsConsistency.Mixed, RevenueDirection.Stable);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(0.5m);
    }

    [Fact]
    public async Task ScoreAsync_ReasoningIsAlwaysTheDeterministicTemplate()
    {
        // Claude was removed from fundamentals scoring entirely (30 Jul 2026
        // cost cut) - the reasoning must come from the template and never be
        // empty, with no Claude client involvement at all.
        var snapshot = Snapshot(AnalystTrend.StronglyBullish, InsiderActivity.StrongBuying, EarningsConsistency.ConsistentBeater, RevenueDirection.Accelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(1.0m);
        result.Reasoning.Should().NotBeNullOrWhiteSpace();
        await _claude.DidNotReceive().SendMessageAsync(Arg.Any<ClaudeRequest>());
    }

    [Fact]
    public async Task ScoreAsync_InsufficientDataEnumValues_TreatedAsNeutralNotPenalised()
    {
        var snapshot = Snapshot(AnalystTrend.Insufficient, InsiderActivity.Neutral, EarningsConsistency.Insufficient, RevenueDirection.Insufficient);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(0.5m);
    }

    [Fact]
    public async Task ScoreAsync_SurpriseAcceleration_TiltsTheEarningsSubScore_Bounded()
    {
        var baseSnapshot = Snapshot(AnalystTrend.Neutral, InsiderActivity.Neutral, EarningsConsistency.RecentBeater, RevenueDirection.Stable);

        var flat = await CreateSut().ScoreAsync(_claude, "AAPL", baseSnapshot, CancellationToken.None);
        // +10pp trend = half the ±20pp saturation -> half the max 0.15 tilt on
        // the earnings sub-score, at 0.25 sub-weight = +0.075 * 0.25 ≈ +0.019.
        var accelerating = await CreateSut().ScoreAsync(
            _claude, "AAPL", baseSnapshot with { SurpriseTrendPct = 10m }, CancellationToken.None);
        // A huge trend saturates at the cap rather than growing unbounded.
        var saturated = await CreateSut().ScoreAsync(
            _claude, "AAPL", baseSnapshot with { SurpriseTrendPct = 200m }, CancellationToken.None);
        var shrinking = await CreateSut().ScoreAsync(
            _claude, "AAPL", baseSnapshot with { SurpriseTrendPct = -10m }, CancellationToken.None);

        accelerating.Score.Should().BeGreaterThan(flat.Score);
        shrinking.Score.Should().BeLessThan(flat.Score);
        saturated.Score.Should().Be(flat.Score + 0.15m * 0.25m); // full tilt x earnings sub-weight
    }
}
