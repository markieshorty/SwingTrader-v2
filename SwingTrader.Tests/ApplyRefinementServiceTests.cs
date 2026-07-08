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
        // FundamentalMomentumWeight that differs from the 0.10 class default
        // so a missed copy is detectable.
        var suggested = new StrategyWeights
        {
            RsiWeight = 0.15m,
            MacdWeight = 0.10m,
            VolumeWeight = 0.20m,
            SentimentWeight = 0.15m,
            SetupQualityWeight = 0.10m,
            RelativeStrengthWeight = 0.10m,
            PriceLevelWeight = 0.05m,
            FundamentalMomentumWeight = 0.15m,
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
            w.FundamentalMomentumWeight == 0.15m));
    }
}
