using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Agents.Refinement;
using SwingTrader.Agents.Sharing;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class StrategyShareServiceTests
{
    private readonly IStrategyWeightsRepository _weightsRepo = Substitute.For<IStrategyWeightsRepository>();
    private readonly IAccountRiskProfileRepository _riskRepo = Substitute.For<IAccountRiskProfileRepository>();
    private readonly ISetupTacticsRepository _tacticsRepo = Substitute.For<ISetupTacticsRepository>();
    private readonly IRefinementSuggestionRepository _suggestionRepo = Substitute.For<IRefinementSuggestionRepository>();
    private readonly IApplyRefinementService _applyService = Substitute.For<IApplyRefinementService>();
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly StrategyShareService _sut;

    private const int SenderId = 1;
    private const int RecipientId = 2;

    public StrategyShareServiceTests()
    {
        _sut = new StrategyShareService(
            _weightsRepo, _riskRepo, _tacticsRepo, _suggestionRepo, _applyService, _accountRepo,
            NullLogger<StrategyShareService>.Instance);
    }

    private static StrategyWeights SenderWeights() => new()
    {
        AccountId = SenderId,
        RsiWeight = 0.20m, MacdWeight = 0.15m, VolumeWeight = 0.25m,
        SetupQualityWeight = 0.18m, RelativeStrengthWeight = 0.15m, PriceLevelWeight = 0.07m,
        ForwardSentimentWeight = 0.45m, ForwardFundamentalWeight = 0.30m, ForwardFilingWeight = 0.25m,
        BuyThreshold = 6.2m, WatchThreshold = 5.1m, StopLossPctDefault = 0.055m,
    };

    private static List<AccountRiskProfile> SenderBooks() =>
        new[] { MarketRegime.Default, MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear, MarketRegime.Crisis }
            .Select((regime, i) => new AccountRiskProfile
            {
                AccountId = SenderId, Regime = regime,
                Enabled = regime == MarketRegime.Default,
                AutopauseTrading = regime is MarketRegime.Bear or MarketRegime.Crisis,
                LockedCapitalPct = 0.10m + 0.05m * i, MaxOpenPositions = 3 + i,
                DailyLossCircuitBreakerPct = 0.03m, MaxHoldDays = 10 + i,
                TrailingActivationPct = 0.05, TrailingDistancePct = 0.03,
                EarningsGateDays = 2, MinHoldDays = 3, MomentumHealthThreshold = 0.5m,
                StopLossPct = 0.05m, TargetPct = 0.08m,
                SizingMode = PositionSizingMode.Flat, FlatPositionPct = 0.10m,
                SizingAggressiveness = 0.1m * i, ForwardVetoFloor = 0.2m,
            }).ToList();

    private static List<SetupTactics> SenderTactics() =>
        new[] { SetupType.OversoldRecovery, SetupType.Breakout, SetupType.VolumeSpike }
            .Select((setup, i) => new SetupTactics
            {
                AccountId = SenderId, SetupType = setup,
                Enabled = setup != SetupType.VolumeSpike,
                StopLossPct = 0.05m + 0.005m * i, TargetPct = 0.08m + 0.005m * i,
                GuideHoldDays = 8 + i, TrailingActivationPct = 0.05, TrailingDistancePct = 0.03,
            }).ToList();

    [Fact]
    public async Task BuildSnapshot_CapturesWeightsBooksAndTactics()
    {
        _weightsRepo.GetActiveWeightsAsync(SenderId).Returns(SenderWeights());
        _riskRepo.GetAllAsync(SenderId, Arg.Any<CancellationToken>()).Returns(SenderBooks());
        _tacticsRepo.GetAllAsync(SenderId, Arg.Any<CancellationToken>()).Returns(SenderTactics());

        var snap = await _sut.BuildSnapshotAsync(SenderId);

        snap.Weights.BuyThreshold.Should().Be(6.2m);
        snap.Weights.StopLossPctDefault.Should().Be(0.055m);
        snap.Weights.ForwardFilingWeight.Should().Be(0.25m);
        snap.RiskBooks.Should().HaveCount(5);
        snap.RiskBooks.Single(b => b.Regime == "Default").Enabled.Should().BeTrue();
        snap.RiskBooks.Single(b => b.Regime == "Bear").AutopauseTrading.Should().BeTrue();
        snap.RiskBooks.Single(b => b.Regime == "Crisis").MaxOpenPositions.Should().Be(7);
        snap.SetupTactics.Should().HaveCount(3);
        snap.SetupTactics.Single(t => t.SetupType == "VolumeSpike").Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SnapshotJson_RoundTrips_Losslessly()
    {
        _weightsRepo.GetActiveWeightsAsync(SenderId).Returns(SenderWeights());
        _riskRepo.GetAllAsync(SenderId, Arg.Any<CancellationToken>()).Returns(SenderBooks());
        _tacticsRepo.GetAllAsync(SenderId, Arg.Any<CancellationToken>()).Returns(SenderTactics());

        var snap = await _sut.BuildSnapshotAsync(SenderId);
        var json = JsonSerializer.Serialize(snap, StrategyShareService.SnapshotJsonOptions);
        var back = JsonSerializer.Deserialize<StrategySnapshot>(json, StrategyShareService.SnapshotJsonOptions);

        back.Should().BeEquivalentTo(snap);
    }

    [Fact]
    public async Task ApplySnapshot_OverwritesBooksAndTactics_AndAuditsWeightsAsSharedStrategy()
    {
        _weightsRepo.GetActiveWeightsAsync(SenderId).Returns(SenderWeights());
        _riskRepo.GetAllAsync(SenderId, Arg.Any<CancellationToken>()).Returns(SenderBooks());
        _tacticsRepo.GetAllAsync(SenderId, Arg.Any<CancellationToken>()).Returns(SenderTactics());
        var snapshot = await _sut.BuildSnapshotAsync(SenderId);

        // Recipient side: books/tactics with different values, mutable so the
        // apply's UpdateAsync writes can be round-tripped back out.
        _accountRepo.GetAsync(RecipientId, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = RecipientId, TradingMode = TradingMode.Demo });
        _weightsRepo.GetActiveWeightsAsync(RecipientId).Returns(new StrategyWeights { AccountId = RecipientId });

        var recipientBooks = SenderBooks();
        foreach (var b in recipientBooks)
        {
            b.AccountId = RecipientId;
            b.Enabled = false;
            b.AutopauseTrading = false;
            b.MaxOpenPositions = 99;
        }
        _riskRepo.GetAsync(RecipientId, Arg.Any<MarketRegime>(), Arg.Any<CancellationToken>())
            .Returns(ci => recipientBooks.Single(b => b.Regime == ci.Arg<MarketRegime>()));

        var recipientTactics = SenderTactics();
        foreach (var t in recipientTactics)
        {
            t.AccountId = RecipientId;
            t.Enabled = true;
            t.StopLossPct = 0.09m;
        }
        _tacticsRepo.GetAllAsync(RecipientId, Arg.Any<CancellationToken>()).Returns(recipientTactics);

        RefinementSuggestion? createdSuggestion = null;
        _suggestionRepo.AddAsync(Arg.Any<RefinementSuggestion>()).Returns(ci =>
        {
            createdSuggestion = ci.Arg<RefinementSuggestion>();
            createdSuggestion.Id = 77;
            return createdSuggestion;
        });
        _applyService.ApplyAsync(RecipientId, 77, null, Arg.Any<CancellationToken>())
            .Returns(new ApplyRefinementResult(true, null, 123));

        await _sut.ApplySnapshotAsync(RecipientId, snapshot, "test apply");

        // Weights flowed through the audit trail with the new origin.
        createdSuggestion.Should().NotBeNull();
        createdSuggestion!.Origin.Should().Be(RefinementOrigin.SharedStrategy);
        var suggested = JsonSerializer.Deserialize<StrategyWeights>(createdSuggestion.SuggestedWeightsJson)!;
        suggested.BuyThreshold.Should().Be(6.2m);
        suggested.RsiWeight.Should().Be(0.20m);
        await _applyService.Received(1).ApplyAsync(RecipientId, 77, null, Arg.Any<CancellationToken>());

        // Books and tactics were overwritten in place (Validate-gated repos).
        await _riskRepo.Received(5).UpdateAsync(Arg.Any<AccountRiskProfile>(), Arg.Any<CancellationToken>());
        recipientBooks.Single(b => b.Regime == MarketRegime.Default).Enabled.Should().BeTrue();
        recipientBooks.Single(b => b.Regime == MarketRegime.Bear).AutopauseTrading.Should().BeTrue();
        recipientBooks.Single(b => b.Regime == MarketRegime.Crisis).MaxOpenPositions.Should().Be(7);
        recipientTactics.Single(t => t.SetupType == SetupType.VolumeSpike).Enabled.Should().BeFalse();
        recipientTactics.Single(t => t.SetupType == SetupType.OversoldRecovery).StopLossPct.Should().Be(0.05m);

        // Round-trip: the recipient's rebuilt snapshot now matches the shared
        // one for everything the service owns directly (books + tactics).
        _weightsRepo.GetActiveWeightsAsync(RecipientId).Returns(SenderWeights());
        _riskRepo.GetAllAsync(RecipientId, Arg.Any<CancellationToken>()).Returns(recipientBooks);
        var rebuilt = await _sut.BuildSnapshotAsync(RecipientId);
        rebuilt.RiskBooks.Should().BeEquivalentTo(snapshot.RiskBooks);
        rebuilt.SetupTactics.Should().BeEquivalentTo(snapshot.SetupTactics);
        rebuilt.Weights.Should().BeEquivalentTo(snapshot.Weights);
    }

    [Fact]
    public async Task ApplySnapshot_WhenWeightsApplyFails_Throws()
    {
        _accountRepo.GetAsync(RecipientId, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = RecipientId });
        _weightsRepo.GetActiveWeightsAsync(RecipientId).Returns(new StrategyWeights());
        _suggestionRepo.AddAsync(Arg.Any<RefinementSuggestion>()).Returns(ci =>
        {
            var s = ci.Arg<RefinementSuggestion>();
            s.Id = 5;
            return s;
        });
        _applyService.ApplyAsync(RecipientId, 5, null, Arg.Any<CancellationToken>())
            .Returns(new ApplyRefinementResult(false, "boom", null));

        var snapshot = new StrategySnapshot(
            new SnapshotWeights(0.23m, 0.12m, 0.28m, 0.16m, 0.14m, 0.07m, 0.45m, 0.30m, 0.25m, 6.0m, 5.0m, 0.05m),
            [], []);

        var act = () => _sut.ApplySnapshotAsync(RecipientId, snapshot, "test");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*boom*");
    }
}
