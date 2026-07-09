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
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly IActivityLogRepository _activityLog = Substitute.For<IActivityLogRepository>();

    public MonitorServiceFillReconciliationTests()
    {
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = 1, TradingMode = TradingMode.Demo });
    }

    private MonitorService CreateSut() => new(
        _tradeRepo, _portfolioRepo, _circuitBreaker, _positionMonitor, _riskProfileRepo,
        _positionExit, _recipients, _emailService, _accountRepo, _activityLog,
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
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>());
    }

    private static HistoricalOrdersResponse HistoryWithFilledOrder(
        long orderId, decimal price, decimal qty, decimal? netValueGbp = null, decimal? realisedProfitLossGbp = null,
        List<HistoricalFillTax>? taxes = null) =>
        new(
            [new HistoricalOrderItem(
                new HistoricalOrderDetail(orderId, "AAPL_US_EQ", "FILLED", qty, qty, price * qty),
                new HistoricalFillDetail(DateTime.UtcNow, price, qty,
                    netValueGbp.HasValue ? new HistoricalFillWalletImpact(netValueGbp.Value, realisedProfitLossGbp, taxes) : null))],
            null);

    [Fact]
    public async Task RunCycleAsync_EntryFillHasConversionFee_CapturesPositiveFeeAmount()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Open, EntryOrderId = "111",
            },
        });
        // Fee quantity is reported negative (a deduction) by T212.
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(
            111, 99.5m, 10m, netValueGbp: 74.60m,
            taxes: [new HistoricalFillTax("CURRENCY_CONVERSION_FEE", -0.15m)]));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t => t.EntryFeesGbp == 0.15m));
    }

    [Fact]
    public async Task RunCycleAsync_ImplausibleEntryFill_KeepsPlacementPriceButStillConfirms()
    {
        // Regression: T212 demo returned an implausible fill price (165) for a
        // ~34.86 placement, which used to overwrite EntryPrice and make the
        // position read as a ~-79% loss. The bad price must be rejected (keep
        // the placement price) yet the order still marked confirmed so it isn't
        // re-pulled every cycle.
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "HAL", Quantity = 3.936m, EntryPrice = 34.86m,
                Status = TradeStatus.Open, EntryOrderId = "222",
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(222, 165m, 3.936m, netValueGbp: 554.36m));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.EntryPrice == 34.86m && t.EntryFillConfirmedAt != null && t.EntryValueGbp == null));
    }

    [Fact]
    public async Task RunCycleAsync_ExitOrderFilledInHistory_UsesT212RealisedPnlNotPriceEstimate()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Closed, ExitPrice = 105m, ExitOrderId = "555",
                EntryOrderId = "111", EntryFillConfirmedAt = DateTime.UtcNow,
            },
        });
        // T212 mirrors the signed quantity of the original order request - a
        // sell (PositionExitService places -trade.Quantity) reports a
        // negative filledQuantity, not positive. Confirmed live: this exact
        // shape (negative qty, positive fill price) is what caused every
        // real exit order to silently never confirm despite being FILLED.
        // realisedProfitLoss (£8.40) deliberately differs from what a naive
        // (fillPrice - EntryPrice) * Quantity estimate (£65) would give -
        // T212's own figure, which accounts for FX/fees, must win.
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(555, 106.5m, -10m, netValueGbp: 78.30m, realisedProfitLossGbp: 8.40m));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.ExitPrice == 106.5m && t.ExitValueGbp == 78.30m && t.RealizedPnl == 8.40m && t.ExitFillConfirmedAt != null));
    }

    [Fact]
    public async Task RunCycleAsync_ExitFillMissingWalletImpact_FallsBackToPriceEstimate()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Closed, ExitPrice = 105m, ExitOrderId = "555",
                EntryOrderId = "111", EntryFillConfirmedAt = DateTime.UtcNow,
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(555, 106.5m, -10m));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.ExitPrice == 106.5m && t.RealizedPnl == (106.5m - 100m) * 10m && t.ExitValueGbp == null));
    }

    [Fact]
    public async Task RunCycleAsync_EntryOrderFilledInHistory_CapturesEntryValueGbp()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Open, EntryOrderId = "111",
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(111, 99.5m, 10m, netValueGbp: 74.60m));

        var sut = CreateSut();
        await sut.RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.EntryPrice == 99.5m && t.EntryValueGbp == 74.60m && t.EntryFillConfirmedAt != null));
    }

    [Fact]
    public async Task RunCycleAsync_OrderNotYetInHistory_LeavesUnconfirmedForNextCycle()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
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
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>
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

    // ── Intent-first Pending reconciliation ──────────────────────────────────
    // A Pending trade is an execution intent written before the broker call
    // whose outcome was left unknown (crash/timeout mid-placement). Monitor
    // resolves it against T212 order history: promote to Open if the order
    // actually placed, or Cancel it once a grace window proves it never did.

    [Fact]
    public async Task RunCycleAsync_PendingOrderFoundInHistory_PromotedToOpenWithFill()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetPendingTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Pending, EntryOrderId = null, OpenedAt = DateTime.UtcNow,
            },
        });
        // A filled order for the same ticker at/after the intent time = the
        // placement really did reach the broker.
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(111, 99.5m, 10m, netValueGbp: 74.60m));

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t =>
            t.Status == TradeStatus.Open && t.EntryOrderId == "111"
            && t.EntryPrice == 99.5m && t.EntryValueGbp == 74.60m && t.EntryFillConfirmedAt != null));
    }

    [Fact]
    public async Task RunCycleAsync_PendingOrderNoMatchPastGrace_CancelledAndLogged()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetPendingTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Pending, EntryOrderId = null,
                OpenedAt = DateTime.UtcNow.AddMinutes(-40), // past the 30m grace
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(new HistoricalOrdersResponse([], null));

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t => t.Status == TradeStatus.Cancelled));
        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Order Not Placed", "Warning",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_PendingOrderNoMatchWithinGrace_LeftPendingForRetry()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetPendingTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Pending, EntryOrderId = null,
                OpenedAt = DateTime.UtcNow.AddMinutes(-2), // still within grace
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(new HistoricalOrdersResponse([], null));

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        // Neither promoted nor cancelled - waits for T212 history to catch up.
        await _tradeRepo.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
        await _activityLog.DidNotReceive().LogAsync(1, "SystemEvent", "Order Not Placed",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_PendingOrderSellFillForSameTicker_NotAdoptedAsEntry()
    {
        // T212 mirrors the signed order quantity, so a sell fill reports
        // negative. A same-ticker sell post-dating the intent must never be
        // adopted as this intent's entry - its price/realised P&L would
        // corrupt the trade. Within grace -> left Pending for retry.
        SetupNoOpenPositions();
        _tradeRepo.GetPendingTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Pending, EntryOrderId = null,
                OpenedAt = DateTime.UtcNow.AddMinutes(-2),
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(HistoryWithFilledOrder(999, 106.5m, -10m, netValueGbp: 78.30m, realisedProfitLossGbp: 8.40m));

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _tradeRepo.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task RunCycleAsync_PendingOrderStaleSameTickerFromEarlier_NotMatchedByTime()
    {
        // A filled order for the same ticker but *before* the intent time (e.g.
        // a previous day's trade) must not be mistaken for this intent's fill.
        SetupNoOpenPositions();
        _tradeRepo.GetPendingTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>
        {
            new()
            {
                Id = 1, AccountId = 1, Symbol = "AAPL", Quantity = 10, EntryPrice = 100m,
                Status = TradeStatus.Pending, EntryOrderId = null,
                OpenedAt = DateTime.UtcNow.AddMinutes(-40),
            },
        });
        _t212.GetOrderHistoryAsync(50, null, null).Returns(new HistoricalOrdersResponse(
            [new HistoricalOrderItem(
                new HistoricalOrderDetail(111, "AAPL_US_EQ", "FILLED", 10m, 10m, 995m),
                new HistoricalFillDetail(DateTime.UtcNow.AddDays(-1), 99.5m, 10m, null))],
            null));

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        // No time-valid match → cancelled as never-placed, not promoted to Open.
        await _tradeRepo.Received(1).UpdateAsync(Arg.Is<Trade>(t => t.Status == TradeStatus.Cancelled));
    }

    // ── Position-drift reconciliation (local open positions vs broker holdings) ─

    private static PortfolioPositionResponse BrokerPosition(string ticker, decimal qty) =>
        new(ticker, qty, 100m, 100m, 0m, null, null, null, null, null, null);

    private static Trade ConfirmedOpen(string symbol, decimal qty) => new()
    {
        Id = 1, AccountId = 1, Symbol = symbol, Quantity = qty, EntryPrice = 100m,
        Status = TradeStatus.Open, EntryOrderId = "111", EntryFillConfirmedAt = DateTime.UtcNow.AddMinutes(-60),
        OpenedAt = DateTime.UtcNow.AddMinutes(-60), // settled, past the 20-min grace
    };

    [Fact]
    public async Task RunCycleAsync_LocalOpenNotHeldAtBroker_FlagsPositionDrift()
    {
        // Broker still holds OTHER positions (so the response is trusted), but
        // not this one - a genuine per-position phantom.
        SetupNoOpenPositions();
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade> { ConfirmedOpen("AAPL", 10m) });
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse> { BrokerPosition("MSFT_US_EQ", 5m) });

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Position Drift", "Warning",
            Arg.Is<string>(m => m.Contains("AAPL") && m.Contains("not held at the broker")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_BrokerReportsNoHoldingsAtAll_RaisesOneAggregatedAlertNotPerPosition()
    {
        // A 200-with-empty-array while settled positions exist locally is more
        // likely a degraded T212 response (or a full manual liquidation) than
        // N simultaneous unrecorded exits - one aggregated alert, not one per
        // position per cycle.
        SetupNoOpenPositions();
        var aapl = ConfirmedOpen("AAPL", 10m);
        var msft = ConfirmedOpen("MSFT", 5m);
        msft.Id = 2;
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade> { aapl, msft });
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse>());

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Position Drift", "Warning",
            Arg.Is<string>(m => m.Contains("no holdings at all") && m.Contains("AAPL") && m.Contains("MSFT")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_BrokerHoldingWithNoLocalRecord_FlagsPositionDrift()
    {
        SetupNoOpenPositions(); // no local open trades
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse> { BrokerPosition("TSLA_US_EQ", 5m) });

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Position Drift", "Warning",
            Arg.Is<string>(m => m.Contains("TSLA_US_EQ") && m.Contains("no matching open position")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_QuantityMismatch_FlagsPositionDrift()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade> { ConfirmedOpen("AAPL", 10m) });
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse> { BrokerPosition("AAPL_US_EQ", 7m) });

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Position Drift", "Warning",
            Arg.Is<string>(m => m.Contains("quantity mismatch")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_LocalAndBrokerMatch_NoDrift()
    {
        SetupNoOpenPositions();
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade> { ConfirmedOpen("AAPL", 10m) });
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse> { BrokerPosition("AAPL_US_EQ", 10m) });

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _activityLog.DidNotReceive().LogAsync(1, "SystemEvent", "Position Drift", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_FreshUnsettledPosition_NotFlaggedAsPhantom()
    {
        // A position opened moments ago (within the grace window) may not be in
        // T212's portfolio yet - it must not be flagged as drift.
        SetupNoOpenPositions();
        var fresh = ConfirmedOpen("AAPL", 10m);
        fresh.OpenedAt = DateTime.UtcNow.AddMinutes(-2);
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade> { fresh });
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse>());

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _activityLog.DidNotReceive().LogAsync(1, "SystemEvent", "Position Drift", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_SnapshotCarriesForwardEarnedTier_NotHardcodedTier1()
    {
        // Regression: Monitor's per-cycle snapshot hardcoded CurrentTier=Tier1,
        // so an earned Tier2/Tier3 (applied by TierEvaluationService onto the
        // then-latest snapshot) was clobbered back to Tier1 within one 5-minute
        // cycle - ExecutionService sizes from the latest snapshot's tier, so
        // tier progression silently never took effect.
        SetupNoOpenPositions();
        _portfolioRepo.GetLatestSnapshotAsync(1, TradingMode.Demo)
            .Returns(new PortfolioSnapshot { CurrentTier = CapitalTier.Tier2 });

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        await _portfolioRepo.Received(1).AddAsync(Arg.Is<PortfolioSnapshot>(s => s.CurrentTier == CapitalTier.Tier2));
    }

    [Fact]
    public async Task RunCycleAsync_CircuitBreakerTriggers_AutoPausesEntriesForCurrentModeAndLogs()
    {
        var account = new Account { Id = 1, TradingMode = TradingMode.Demo };
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(account);
        _t212.GetAccountSummaryAsync().Returns(new T212AccountSummary(
            1000m, new T212AccountSummaryCash(1000m, 0m, 0m), new T212AccountSummaryInvestments(0m, 0m, 0m, 0m)));
        _circuitBreaker.ShouldTriggerAsync(1, Arg.Any<T212AccountSummary?>(), Arg.Any<CancellationToken>()).Returns(true);
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>());

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        account.ExecutionPausedDemo.Should().BeTrue();
        account.ExecutionPauseReasonDemo.Should().Be(ExecutionPauseReason.CircuitBreaker);
        account.ExecutionPausedAtDemo.Should().NotBeNull();
        account.ExecutionPausedLive.Should().BeFalse(); // per-mode: Live untouched
        await _accountRepo.Received(1).UpdateAsync(account, Arg.Any<CancellationToken>());
        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Entries Auto-Paused", "Warning",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_CircuitBreakerTriggersButAlreadyPaused_DoesNotRewriteAccount()
    {
        // Already paused (e.g. manually) - the breaker must not overwrite the
        // reason/timestamp or hit the DB again every cycle.
        var account = new Account { Id = 1, TradingMode = TradingMode.Demo, ExecutionPausedDemo = true, ExecutionPauseReasonDemo = ExecutionPauseReason.Manual };
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(account);
        _t212.GetAccountSummaryAsync().Returns(new T212AccountSummary(
            1000m, new T212AccountSummaryCash(1000m, 0m, 0m), new T212AccountSummaryInvestments(0m, 0m, 0m, 0m)));
        _circuitBreaker.ShouldTriggerAsync(1, Arg.Any<T212AccountSummary?>(), Arg.Any<CancellationToken>()).Returns(true);
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>());

        await CreateSut().RunCycleAsync(1, _finnhub, _t212);

        account.ExecutionPauseReasonDemo.Should().Be(ExecutionPauseReason.Manual);
        await _accountRepo.DidNotReceive().UpdateAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
        await _activityLog.DidNotReceive().LogAsync(1, "SystemEvent", "Entries Auto-Paused", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
