using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Agents.Refinement;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class ApplyRefinementServiceTests
{
    private readonly IRefinementSuggestionRepository _suggestionRepo = Substitute.For<IRefinementSuggestionRepository>();
    private readonly IStrategyWeightsRepository _weightsRepo = Substitute.For<IStrategyWeightsRepository>();
    private readonly ApplyRefinementService _sut;

    public ApplyRefinementServiceTests()
    {
        _sut = new ApplyRefinementService(_suggestionRepo, _weightsRepo, NullLogger<ApplyRefinementService>.Instance);
        _weightsRepo.AddAsync(Arg.Any<StrategyWeights>()).Returns(ci =>
        {
            var w = ci.Arg<StrategyWeights>();
            // Mirror the real repository, which validates before saving -
            // this is exactly where the missing FundamentalMomentumWeight
            // copy used to make every apply throw (weights summed to 1.10).
            w.Validate();
            w.Id = 42;
            return w;
        });
    }

    [Fact]
    public async Task ApplyAsync_CopiesFundamentalMomentumWeight_AndPassesValidation()
    {
        // A realistic suggestion: weights normalised to sum to 1.0, with a
        // A PriceLevelWeight that differs from the class default so a missed
        // copy is detectable. Six gate weights sum to 1.0.
        var suggested = new StrategyWeights
        {
            RsiWeight = 0.15m,
            MacdWeight = 0.10m,
            VolumeWeight = 0.25m,
            SetupQualityWeight = 0.20m,
            RelativeStrengthWeight = 0.15m,
            PriceLevelWeight = 0.15m,
        };

        _suggestionRepo.GetByIdAsync(1, 7).Returns(new RefinementSuggestion
        {
            Id = 7,
            AccountId = 1,
            Status = RefinementStatus.Pending,
            IsShadowMode = false,
            SuggestedWeightsJson = JsonSerializer.Serialize(suggested),
        });

        var result = await _sut.ApplyAsync(1, 7);

        result.Success.Should().BeTrue(result.Error);
        await _weightsRepo.Received(1).AddAsync(Arg.Is<StrategyWeights>(w =>
            w.PriceLevelWeight == 0.15m));
    }

    private static StrategyWeights NormalisedWeights() => new()
    {
        RsiWeight = 0.15m, MacdWeight = 0.10m, VolumeWeight = 0.25m,
        SetupQualityWeight = 0.20m, RelativeStrengthWeight = 0.15m, PriceLevelWeight = 0.15m,
    };

    [Fact]
    public async Task ApplyAsync_ReApplyingHistoricalSuggestion_CreatesWeights_ButLeavesStatusUntouched()
    {
        // Re-applying an already-Applied suggestion from the history list must
        // create a fresh active weights row without rewriting the old
        // suggestion's status/AppliedAt (its audit trail is preserved).
        var appliedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var suggestion = new RefinementSuggestion
        {
            Id = 9,
            AccountId = 1,
            Status = RefinementStatus.Applied,
            AppliedAt = appliedAt,
            IsShadowMode = false,
            SuggestedWeightsJson = JsonSerializer.Serialize(NormalisedWeights()),
        };
        _suggestionRepo.GetByIdAsync(1, 9).Returns(suggestion);

        var result = await _sut.ApplyAsync(1, 9);

        result.Success.Should().BeTrue(result.Error);
        await _weightsRepo.Received(1).AddAsync(Arg.Any<StrategyWeights>());
        await _weightsRepo.Received(1).SetActiveAsync(1, 42);
        // The historical suggestion's own record is not rewritten.
        await _suggestionRepo.DidNotReceive().UpdateAsync(Arg.Any<RefinementSuggestion>());
        suggestion.Status.Should().Be(RefinementStatus.Applied);
        suggestion.AppliedAt.Should().Be(appliedAt);
    }

    [Fact]
    public async Task ApplyAsync_ShadowModeSuggestion_IsRejected()
    {
        _suggestionRepo.GetByIdAsync(1, 5).Returns(new RefinementSuggestion
        {
            Id = 5, AccountId = 1, Status = RefinementStatus.Pending, IsShadowMode = true,
            SuggestedWeightsJson = JsonSerializer.Serialize(NormalisedWeights()),
        });

        var result = await _sut.ApplyAsync(1, 5);

        result.Success.Should().BeFalse();
        await _weightsRepo.DidNotReceive().AddAsync(Arg.Any<StrategyWeights>());
    }
}
