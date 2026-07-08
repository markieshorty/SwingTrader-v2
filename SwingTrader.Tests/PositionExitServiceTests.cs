using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

// The only place in the app that places a real T212 sell order - covers
// ticker resolution, the negative-quantity sell convention, trade mutation
// on success, and the same-day Execution re-enqueue added alongside fill
// reconciliation this session.
public class PositionExitServiceTests
{
    private readonly ITradeRepository _tradeRepo = Substitute.For<ITradeRepository>();
    private readonly IJobLogRepository _jobLog = Substitute.For<IJobLogRepository>();
    private readonly INotificationRecipientRepository _recipients = Substitute.For<INotificationRecipientRepository>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ITrading212Client _t212 = Substitute.For<ITrading212Client>();

    private PositionExitService CreateSut() => new(
        _tradeRepo, _jobLog, _recipients, _emailService,
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new ExecutionConfig { DelayBetweenOrdersSeconds = 0 }),
        NullLogger<PositionExitService>.Instance);

    private static Trade MakeOpenTrade(string symbol = "AAPL", decimal entryPrice = 100m, decimal quantity = 10m) => new()
    {
        Id = 1, AccountId = 1, Symbol = symbol, EntryPrice = entryPrice, Quantity = quantity,
        Status = TradeStatus.Open, OpenedAt = DateTime.UtcNow.AddDays(-3),
    };

    private void SetupInstrument(string symbol, string ticker)
    {
        _t212.GetInstrumentsAsync().Returns(new List<InstrumentResponse>
        {
            new(ticker, symbol, "STOCK", "USD", "US0000000000"),
        });
    }

    private void SetupNoRecipients() => _recipients.ListAsync(1).Returns(new List<NotificationRecipient>());

    [Fact]
    public async Task ClosePositionAsync_Success_PlacesNegativeQuantitySellAndUpdatesTrade()
    {
        SetupInstrument("AAPL", "AAPL_US_EQ");
        SetupNoRecipients();
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(999, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        var trade = MakeOpenTrade();

        var sut = CreateSut();
        var result = await sut.ClosePositionAsync(1, trade, _t212, currentPrice: 110m, ExitReason.TargetHit, "hit target");

        result.Success.Should().BeTrue();
        result.ExitPrice.Should().Be(110m);
        result.RealizedPnl.Should().Be((110m - 100m) * 10m);

        await _t212.Received(1).PlaceMarketOrderAsync(Arg.Is<MarketOrderRequest>(r => r.Ticker == "AAPL_US_EQ" && r.Quantity == -10m));

        trade.Status.Should().Be(TradeStatus.Closed);
        trade.ExitPrice.Should().Be(110m);
        trade.ExitOrderId.Should().Be("999");
        trade.RealizedPnl.Should().Be(100m);
        await _tradeRepo.Received(1).UpdateAsync(trade);
    }

    [Fact]
    public async Task ClosePositionAsync_NoMatchingInstrument_ReturnsFailureWithoutPlacingOrder()
    {
        _t212.GetInstrumentsAsync().Returns(new List<InstrumentResponse>());
        var trade = MakeOpenTrade();

        var sut = CreateSut();
        var result = await sut.ClosePositionAsync(1, trade, _t212, 110m, ExitReason.TargetHit, "hit target");

        result.Success.Should().BeFalse();
        await _t212.DidNotReceive().PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>());
        await _tradeRepo.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task ClosePositionAsync_InstrumentLookupThrows_ReturnsFailure()
    {
        _t212.GetInstrumentsAsync().Returns<List<InstrumentResponse>>(_ => throw new HttpRequestException("boom"));
        var trade = MakeOpenTrade();

        var sut = CreateSut();
        var result = await sut.ClosePositionAsync(1, trade, _t212, 110m, ExitReason.StopLossHit, "hit stop");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("boom");
        trade.Status.Should().Be(TradeStatus.Open);
    }

    [Fact]
    public async Task ClosePositionAsync_OrderPlacementThrows_ReturnsFailureAndLeavesTradeOpen()
    {
        SetupInstrument("AAPL", "AAPL_US_EQ");
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns<OrderResponse>(_ => throw new HttpRequestException("T212 down"));
        var trade = MakeOpenTrade();

        var sut = CreateSut();
        var result = await sut.ClosePositionAsync(1, trade, _t212, 110m, ExitReason.TimeExit, "held too long");

        result.Success.Should().BeFalse();
        trade.Status.Should().Be(TradeStatus.Open);
        await _tradeRepo.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task ClosePositionAsync_TickerResolvedByExactMatch_NotJustPrefix()
    {
        // "AAPL" should match the "AAPL_US_EQ" ticker via the "{symbol}_" prefix
        // rule, not accidentally match some other similarly-prefixed ticker.
        _t212.GetInstrumentsAsync().Returns(new List<InstrumentResponse>
        {
            new("AAPLX_US_EQ", "Not Apple", "STOCK", "USD", "US1111111111"),
            new("AAPL_US_EQ", "Apple Inc", "STOCK", "USD", "US0000000000"),
        });
        SetupNoRecipients();
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        var trade = MakeOpenTrade();

        var sut = CreateSut();
        var result = await sut.ClosePositionAsync(1, trade, _t212, 110m, ExitReason.TargetHit, "hit target");

        result.Success.Should().BeTrue();
        await _t212.Received(1).PlaceMarketOrderAsync(Arg.Is<MarketOrderRequest>(r => r.Ticker == "AAPL_US_EQ"));
    }

    [Fact]
    public async Task ClosePositionAsync_ExecutionCompletedToday_ReenqueuesByDeletingJobLog()
    {
        SetupInstrument("AAPL", "AAPL_US_EQ");
        SetupNoRecipients();
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        _jobLog.FindAsync(1, "Execution", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new JobLogEntry { AccountId = 1, JobType = "Execution", Status = JobStatus.Completed });

        var sut = CreateSut();
        await sut.ClosePositionAsync(1, MakeOpenTrade(), _t212, 110m, ExitReason.TargetHit, "hit target");

        await _jobLog.Received(1).DeleteAsync(1, "Execution", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClosePositionAsync_ExecutionStillProcessing_DoesNotReenqueue()
    {
        // Avoids a race where an in-flight Execution run's own
        // MarkCompletedAsync would no-op against a deleted row while a
        // second run gets enqueued alongside it.
        SetupInstrument("AAPL", "AAPL_US_EQ");
        SetupNoRecipients();
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        _jobLog.FindAsync(1, "Execution", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new JobLogEntry { AccountId = 1, JobType = "Execution", Status = JobStatus.Processing });

        var sut = CreateSut();
        await sut.ClosePositionAsync(1, MakeOpenTrade(), _t212, 110m, ExitReason.TargetHit, "hit target");

        await _jobLog.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClosePositionAsync_NoExecutionJobToday_DoesNotReenqueue()
    {
        SetupInstrument("AAPL", "AAPL_US_EQ");
        SetupNoRecipients();
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        _jobLog.FindAsync(1, "Execution", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((JobLogEntry?)null);

        var sut = CreateSut();
        await sut.ClosePositionAsync(1, MakeOpenTrade(), _t212, 110m, ExitReason.TargetHit, "hit target");

        await _jobLog.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClosePositionAsync_ReenqueueLookupThrows_StillReturnsOverallSuccess()
    {
        // Not worth failing a successfully-placed exit over a re-enqueue
        // hiccup - Execution just runs on its normal schedule instead.
        SetupInstrument("AAPL", "AAPL_US_EQ");
        SetupNoRecipients();
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        _jobLog.FindAsync(1, "Execution", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<JobLogEntry?>(_ => throw new InvalidOperationException("db unavailable"));

        var sut = CreateSut();
        var result = await sut.ClosePositionAsync(1, MakeOpenTrade(), _t212, 110m, ExitReason.TargetHit, "hit target");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ClosePositionAsync_RecipientWithExecutionCategory_SendsExitEmail()
    {
        SetupInstrument("AAPL", "AAPL_US_EQ");
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        _recipients.ListAsync(1).Returns(new List<NotificationRecipient>
        {
            new() { Email = "owner@example.com", Categories = NotificationCategory.Execution },
        });

        var sut = CreateSut();
        await sut.ClosePositionAsync(1, MakeOpenTrade(), _t212, 110m, ExitReason.TargetHit, "hit target");

        await _emailService.Received(1).SendSimpleEmailAsync(
            Arg.Is<List<string>>(l => l.Contains("owner@example.com")), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ClosePositionAsync_RecipientWithoutExecutionCategory_DoesNotSendEmail()
    {
        SetupInstrument("AAPL", "AAPL_US_EQ");
        _t212.PlaceMarketOrderAsync(Arg.Any<MarketOrderRequest>()).Returns(
            new OrderResponse(1, "MARKET", "AAPL_US_EQ", -10m, null, null, "NEW", null, "2026-01-01", null));
        _recipients.ListAsync(1).Returns(new List<NotificationRecipient>
        {
            new() { Email = "other@example.com", Categories = NotificationCategory.DailyReport },
        });

        var sut = CreateSut();
        await sut.ClosePositionAsync(1, MakeOpenTrade(), _t212, 110m, ExitReason.TargetHit, "hit target");

        await _emailService.DidNotReceive().SendSimpleEmailAsync(Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
