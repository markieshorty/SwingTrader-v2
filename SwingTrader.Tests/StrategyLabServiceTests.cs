using FluentAssertions;
using NSubstitute;
using SwingTrader.Agents.Refinement;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Strategy Lab own-data replay: re-scores the account's closed trades under
// candidate dials via the shared TradeReplay spine.
public class StrategyLabServiceTests
{
    private readonly ITradeReplayService _replay = Substitute.For<ITradeReplayService>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IStrategyWeightsRepository _weightsRepo = Substitute.For<IStrategyWeightsRepository>();
    private readonly List<ReplayableTrade> _trades = [];

    private StrategyLabService CreateSut() => new(_replay, _accounts, _weightsRepo);

    private static LabWeights EqualWeights() => new(0.125m, 0.125m, 0.125m, 0.125m, 0.125m, 0.125m, 0.125m, 0.125m);

    private int _nextId = 1;

    private void AddTrade(decimal returnPct, decimal allComponentScores, SetupType setup = SetupType.MomentumContinuation)
    {
        var id = _nextId++;
        _trades.Add(new ReplayableTrade(
            new Trade { Id = id, Symbol = $"SYM{id}", OpenedAt = DateTime.UtcNow.AddDays(-id) },
            new StockSignal
            {
                Id = id, SetupType = setup,
                RsiScore = allComponentScores, MacdScore = allComponentScores, VolumeScore = allComponentScores,
                SentimentComponentScore = allComponentScores, SetupQualityScore = allComponentScores,
                RelativeStrengthScore = allComponentScores, PriceLevelScore = allComponentScores,
                FundamentalMomentumScore = allComponentScores,
            },
            returnPct));
    }

    public StrategyLabServiceTests()
    {
        _accounts.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = 1, TradingMode = TradingMode.Demo });
        _replay.LoadAsync(1, TradingMode.Demo, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => _trades.ToList());
    }

    [Fact]
    public async Task Run_ThresholdFiltersLowConvictionTrades()
    {
        // Uniform component scores of 0.9 -> conviction 9.0; 0.4 -> 4.0.
        AddTrade(returnPct: 10m, allComponentScores: 0.9m);  // kept at threshold 6
        AddTrade(returnPct: -10m, allComponentScores: 0.4m); // dropped at threshold 6

        var response = await CreateSut().RunOwnDataAsync(1,
            new StrategyLabRequest("own", EqualWeights(), BuyThreshold: 6.0m, ExcludeBreakout: false), default);

        response!.Result.TotalClosedTrades.Should().Be(2);
        response.Result.TradesKept.Should().Be(1);
        response.Result.DroppedLosers.Should().Be(1);
        response.Result.SimAvgReturnPct.Should().Be(10m);   // only the winner remains
        response.Result.ActualAvgReturnPct.Should().Be(0m); // +10% and -10%
    }

    [Fact]
    public async Task Run_ExcludeBreakout_DropsBreakoutTradesRegardlessOfConviction()
    {
        AddTrade(returnPct: 5m, allComponentScores: 0.9m, setup: SetupType.Breakout);
        AddTrade(returnPct: 5m, allComponentScores: 0.9m, setup: SetupType.OversoldRecovery);

        var response = await CreateSut().RunOwnDataAsync(1,
            new StrategyLabRequest("own", EqualWeights(), 6.0m, ExcludeBreakout: true), default);

        response!.Result.TradesKept.Should().Be(1);
        response.Trades.Single(t => t.Setup == "Breakout").WouldTake.Should().BeFalse();
    }

    [Fact]
    public async Task Run_SuggestionsOnlyOfferRealImprovementsWithSampleFloor()
    {
        // 10 trades: high-conviction ones lose, low-conviction ones win - so
        // any suggestion that raises the threshold cherry-picks losers and must
        // NOT be offered.
        for (var i = 0; i < 5; i++) AddTrade(returnPct: -5m, allComponentScores: 0.9m);
        for (var i = 0; i < 5; i++) AddTrade(returnPct: 5m, allComponentScores: 0.55m);

        var response = await CreateSut().RunOwnDataAsync(1,
            new StrategyLabRequest("own", EqualWeights(), 4.0m, ExcludeBreakout: false), default);

        response!.Suggestions.Should().NotContain(s => s.BuyThreshold > 4.0m && s.SimAvgReturnPct <= response.Result.SimAvgReturnPct);
        response.Suggestions.Should().OnlyContain(s => s.ImprovementPct > 0);
    }

    [Fact]
    public async Task Run_NoReplayableTrades_ReturnsWarningNotError()
    {
        var response = await CreateSut().RunOwnDataAsync(1,
            new StrategyLabRequest("own", EqualWeights(), 6.0m, false), default);

        response!.Warning.Should().Contain("No closed trades");
        response.Result.TotalClosedTrades.Should().Be(0);
        response.Suggestions.Should().BeEmpty();
    }
}
