using FluentAssertions;
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

// GetOrderAsync (T212's single-order lookup) only returns currently-working
// orders and 404s the moment a market order fills - confirmed via live
// production traces where every pending order lookup 404'd, including ones
// placed under 30 minutes earlier. These tests cover the fix: reconciliation
// via GetOrderHistoryAsync instead, matching by order id.
public class MonitorServiceFillReconciliationTests
{
    private readonly ITradeRepository _tradeRepo = Substitute.For<ITradeRepository>();
    private readonly IPortfolioRepository _portfolioRepo = Substitute.For<IPortfolioRepository>();
    private readonly IPortfolioCircuitBreakerService _circuitBreaker = Substitute.For<IPortfolioCircuitBreakerService>();
    private readonly IPositionMonitorService _positionMonitor = Substitute.For<IPositionMonitorService>();
    private readonly IAccountRiskProfileRepository _riskProfileRepo = Substitute.For<IAccountRiskProfileRepository>();
    private readonly IPositionExitService _positionExit = Substitute.For<IPositionExitService>();
    private readonly INotificationRecipientRepository _recipients = Substitute.For<INotificationRecipientRepository>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IFinnhubClient _finnhub = Substitute.For<IFinnhubClient>();
    private readonly ITrading212Client _t212 = Substitute.For<ITrading212Client>();

    private MonitorService CreateSut() => new(
        _tradeRepo, _portfolioRepo, _circuitBreaker, _positionMonitor, _riskProfileRepo,
        _positionExit, _recipients, _emailService,
        Options.Create(new ExecutionConfig { DelayBetweenOrdersSeconds = 0 }),
        NullLogger<MonitorService>.Instance);

    private void SetupNoOpenPositions()
    {
        _t212.GetAccountSummaryAsync().Returns(new T212AccountSummary(
            1000m,
            new T212AccountSummaryCash(1000m, 0m, 0m),
            new T212AccountSummaryInvestments(0m, 0m, 0m, 0m)));
        _circuitBreaker.ShouldTriggerAsync(1, Arg.Any<T212AccountSummary?>(), Arg.Any<CancellationToken>()).Returns(false);
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new AccountRiskProfile { AccountId = 1 });
        _tradeRepo.GetOpenTradesAsync(1).Returns(new List<Trade>());
    }

    private static HistoricalOrdersResponse HistoryWithFilledOrder(long orderId, decimal price, decimal qty) =>
        new(
            [new HistoricalOrderItem(
                new HistoricalOrderDetail(orderId, "AAPL_US_EQ", "FILLED", qty, qty, price * qty),
                new HistoricalFillDetail(DateTime.UtcNow, price, qty))],
            null);

    [Fact]
    public async Task RunCycleAsync_ExitOrderFilledInHistory_UpdatesExitPriceAndRealizedPnl()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Closed, ExitPrice = 105m, ExitOrderId = "555",
                EntryOrderId = "111", EntryFillConfirmedAt = DateTime.UtcNow,
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(555, 106.5m, 10m));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.ExitPrice == 106.5m && t.RealizedPnl == (106.5m - 100m) * 10m && t.ExitFillConfirmedAt != null));
    }

    [Fact]
    public async Task RunCycleAsync_OrderNotYetInHistory_LeavesUnconfirmedForNextCycle()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Closed, ExitPrice = 105m, ExitOrderId = "999",
                EntryOrderId = "111", EntryFillConfirmedAt = DateTime.UtcNow,
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(new HistoricalOrdersResponse([], null));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task RunCycleAsync_OrderCancelledInHistory_KeepsEstimateAndStopsPolling()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Closed, ExitPrice = 105m, ExitOrderId = "777",
                EntryOrderId = "111", EntryFillConfirmedAt = DateTime.UtcNow,
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(
            new HistoricalOrdersResponse(
                [new HistoricalOrderItem(new HistoricalOrderDetail(777, "AAPL_US_EQ", "CANCELLED", 10m, null, null), null)],
                null));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.ExitPrice == 105m && t.ExitFillConfirmedAt != null));
    }
}
