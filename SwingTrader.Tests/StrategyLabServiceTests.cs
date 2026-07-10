using FluentAssertions;
using NSubstitute;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Strategy Lab own-data replay: re-scores the account's closed trades under
// candidate dials using each signal's persisted component scores.
public class StrategyLabServiceTests
{
    private readonly ITradeRepository _trades = Substitute.For<ITradeRepository>();
    private readonly ISignalRepository _signals = Substitute.For<ISignalRepository>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();

    private StrategyLabService CreateSut() => new(_trades, _signals, _accounts);

    private static LabWeights EqualWeights() => new(0.125m, 0.125m, 0.125m, 0.125m, 0.125m, 0.125m, 0.125m, 0.125m);

    private int _nextId = 1;

    private void AddTrade(decimal pnl, decimal allComponentScores, SetupType setup = SetupType.MomentumContinuation)
    {
        var id = _nextId++;
        var trade = new Trade
        {
            Id = id, AccountId = 1, Symbol = $"SYM{id}", Status = TradeStatus.Closed,
            EntryPrice = 100m, Quantity = 1m, RealizedPnl = pnl, SignalId = id,
            OpenedAt = DateTime.UtcNow.AddDays(-id), ClosedAt = DateTime.UtcNow,
        };
        _existing.Add(trade);
        _trades.GetTradeHistoryAsync(1, TradingMode.Demo, Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(_existing.ToList());
        _signals.GetByIdAsync(1, id).Returns(new StockSignal
        {
            Id = id, SetupType = setup,
            RsiScore = allComponentScores, MacdScore = allComponentScores, VolumeScore = allComponentScores,
            SentimentComponentScore = allComponentScores, SetupQualityScore = allComponentScores,
            RelativeStrengthScore = allComponentScores, PriceLevelScore = allComponentScores,
            FundamentalMomentumScore = allComponentScores,
        });
    }

    private readonly List<Trade> _existing = [];

    public StrategyLabServiceTests()
    {
        _accounts.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = 1, TradingMode = TradingMode.Demo });
    }

    [Fact]
    public async Task Run_ThresholdFiltersLowConvictionTrades()
    {
        // Uniform component scores of 0.9 -> conviction 9.0; 0.4 -> 4.0.
        AddTrade(pnl: 10m, allComponentScores: 0.9m);  // kept at threshold 6
        AddTrade(pnl: -10m, allComponentScores: 0.4m); // dropped at threshold 6

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
        AddTrade(pnl: 5m, allComponentScores: 0.9m, setup: SetupType.Breakout);
        AddTrade(pnl: 5m, allComponentScores: 0.9m, setup: SetupType.OversoldRecovery);

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
        // NOT be offered; lowering can't add trades (all already kept at 4.0).
        for (var i = 0; i < 5; i++) AddTrade(pnl: -5m, allComponentScores: 0.9m);
        for (var i = 0; i < 5; i++) AddTrade(pnl: 5m, allComponentScores: 0.55m);

        var response = await CreateSut().RunOwnDataAsync(1,
            new StrategyLabRequest("own", EqualWeights(), 4.0m, ExcludeBreakout: false), default);

        // Raising the threshold keeps only the 0.9-score losers -> worse, so no
        // threshold-raise suggestion may appear.
        response!.Suggestions.Should().NotContain(s => s.BuyThreshold > 4.0m && s.SimAvgReturnPct <= response.Result.SimAvgReturnPct);
        response.Suggestions.Should().OnlyContain(s => s.ImprovementPct > 0);
    }

    [Fact]
    public async Task Run_NoReplayableTrades_ReturnsWarningNotError()
    {
        _trades.GetTradeHistoryAsync(1, TradingMode.Demo, Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Trade>());

        var response = await CreateSut().RunOwnDataAsync(1,
            new StrategyLabRequest("own", EqualWeights(), 6.0m, false), default);

        response!.Warning.Should().Contain("No closed trades");
        response.Result.TotalClosedTrades.Should().Be(0);
        response.Suggestions.Should().BeEmpty();
    }
}
