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

    private FundamentalScoringService CreateSut() =>
        new(Options.Create(new ClaudeConfig()), Options.Create(_config),
            Substitute.For<IClaudeRateLimiter>(), NullLogger<FundamentalScoringService>.Instance);

    private static FundamentalSnapshot Snapshot(
        AnalystTrend analyst, InsiderActivity insider, EarningsConsistency earnings, RevenueDirection revenue) =>
        new("AAPL", analyst, insider, earnings, revenue, AnalystCount: 10, InsiderBuyerCount: 0, InsiderSellerCount: 0,
            NetInsiderShares: null, FetchedAt: DateTime.UtcNow);

    private void FailClaudeCall() =>
        _claude.SendMessageAsync(Arg.Any<ClaudeRequest>()).Returns<Task<ClaudeResponse>>(_ => throw new Exception("no claude in tests"));

    [Fact]
    public async Task ScoreAsync_BestCaseSnapshot_ScoresAtMaximum()
    {
        FailClaudeCall();
        var snapshot = Snapshot(AnalystTrend.StronglyBullish, InsiderActivity.StrongBuying, EarningsConsistency.ConsistentBeater, RevenueDirection.Accelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(1.0m);
    }

    [Fact]
    public async Task ScoreAsync_WorstCaseSnapshot_ScoresNearMinimum()
    {
        FailClaudeCall();
        var snapshot = Snapshot(AnalystTrend.StronglyBearish, InsiderActivity.ClusterSelling, EarningsConsistency.ConsistentMisser, RevenueDirection.Decelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        // ClusterSelling's sub-score is 0.15 (not 0), so the worst case isn't
        // a flat zero - it should still land well below the 0.5 midpoint.
        result.Score.Should().BeLessThan(0.1m);
    }

    [Fact]
    public async Task ScoreAsync_AllNeutralInputs_ScoresAtMidpoint()
    {
        FailClaudeCall();
        var snapshot = Snapshot(AnalystTrend.Neutral, InsiderActivity.Neutral, EarningsConsistency.Mixed, RevenueDirection.Stable);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(0.5m);
    }

    [Fact]
    public async Task ScoreAsync_ScoreIsDeterministic_IndependentOfClaudeOutput()
    {
        // The numeric score must never depend on Claude's response - only the
        // narrative text does - so the Refinement agent can trust it as a
        // stable, reproducible input.
        _claude.SendMessageAsync(Arg.Any<ClaudeRequest>()).Returns(new ClaudeResponse(
            "id", "message", "assistant",
            [new ClaudeContentBlock("text", "Some narrative unrelated to the score.")],
            "model", "end_turn", new ClaudeUsage(10, 10)));
        var snapshot = Snapshot(AnalystTrend.StronglyBullish, InsiderActivity.StrongBuying, EarningsConsistency.ConsistentBeater, RevenueDirection.Accelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(1.0m);
        result.Reasoning.Should().Contain("Some narrative");
    }

    [Fact]
    public async Task ScoreAsync_ClaudeThrows_FallsBackToTemplateReasoningWithoutBlowingUp()
    {
        FailClaudeCall();
        var snapshot = Snapshot(AnalystTrend.StronglyBullish, InsiderActivity.StrongBuying, EarningsConsistency.ConsistentBeater, RevenueDirection.Accelerating);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(1.0m);
        result.Reasoning.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ScoreAsync_InsufficientDataEnumValues_TreatedAsNeutralNotPenalised()
    {
        FailClaudeCall();
        var snapshot = Snapshot(AnalystTrend.Insufficient, InsiderActivity.Neutral, EarningsConsistency.Insufficient, RevenueDirection.Insufficient);

        var result = await CreateSut().ScoreAsync(_claude, "AAPL", snapshot, CancellationToken.None);

        result.Score.Should().Be(0.5m);
    }
}
