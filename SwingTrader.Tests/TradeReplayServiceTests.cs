using FluentAssertions;
using NSubstitute;
using SwingTrader.Agents.Refinement;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The shared data spine for RefinementService and the Strategy Lab: one
// loader, one outcome metric (market-adjusted return), one set of filters.
public class TradeReplayServiceTests
{
    private readonly ITradeRepository _trades = Substitute.For<ITradeRepository>();
    private readonly ISignalRepository _signals = Substitute.For<ISignalRepository>();

    private TradeReplayService CreateSut() => new(_trades, _signals);

    private static Trade ClosedTrade(int id, decimal pnl, decimal? spyReturn = null) => new()
    {
        Id = id, Symbol = $"S{id}", Status = TradeStatus.Closed, ClosedAt = DateTime.UtcNow,
        EntryPrice = 100m, Quantity = 1m, RealizedPnl = pnl, SignalId = id, SpyReturnDuringTrade = spyReturn,
    };

    private void WithSignal(int id) =>
        _signals.GetByIdAsync(1, id).Returns(new StockSignal { Id = id, RsiScore = 0.5m });

    [Fact]
    public async Task Load_SubtractsSpyReturnForMarketAdjustedOutcome()
    {
        // +10% raw during a +4% SPY stretch = +6% market-adjusted; no SPY
        // capture falls back to raw.
        _trades.GetTradeHistoryAsync(1, TradingMode.Demo, Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns([ClosedTrade(1, pnl: 10m, spyReturn: 4m), ClosedTrade(2, pnl: 10m)]);
        WithSignal(1);
        WithSignal(2);

        var result = await CreateSut().LoadAsync(1, TradingMode.Demo, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

        result.Single(r => r.Trade.Id == 1).ReturnPct.Should().Be(6m);
        result.Single(r => r.Trade.Id == 2).ReturnPct.Should().Be(10m);
    }

    [Fact]
    public async Task Load_ExcludesIntentStatesZeroPnlAndUnscoredSignals()
    {
        var pending = ClosedTrade(1, 5m);
        pending.Status = TradeStatus.Pending;
        var zeroPnl = ClosedTrade(2, 0m);
        var unscored = ClosedTrade(3, 5m); // signal without component scores
        var good = ClosedTrade(4, 5m);
        _trades.GetTradeHistoryAsync(1, TradingMode.Demo, Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns([pending, zeroPnl, unscored, good]);
        _signals.GetByIdAsync(1, 3).Returns(new StockSignal { Id = 3, RsiScore = null });
        WithSignal(4);

        var result = await CreateSut().LoadAsync(1, TradingMode.Demo, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

        result.Should().ContainSingle().Which.Trade.Id.Should().Be(4);
    }
}
