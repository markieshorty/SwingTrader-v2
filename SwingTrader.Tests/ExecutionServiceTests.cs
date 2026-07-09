using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.Execution;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

// Covers the approval gate and signal-eligibility filtering (Step 1/2 of
// RunAsync) - both return before the mandatory T212-summary retry/backoff
// delays further down, so these paths exercise real business logic (the
// same-day re-buy guard, approval scoping, WasExecuted exclusion) without a
// real-time wait. The order-placement path beyond that point isn't covered
// here since it always sleeps 15s+ before its first T212 call.
public class ExecutionServiceTests
{
    private readonly ISignalRepository _signalRepo = Substitute.For<ISignalRepository>();
    private readonly ITradeRepository _tradeRepo = Substitute.For<ITradeRepository>();
    private readonly IPortfolioRepository _portfolioRepo = Substitute.For<IPortfolioRepository>();
    private readonly IApprovalRepository _approvalRepo = Substitute.For<IApprovalRepository>();
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly IPositionSizingService _sizing = Substitute.For<IPositionSizingService>();
    private readonly IAccountRiskProfileRepository _riskProfileRepo = Substitute.For<IAccountRiskProfileRepository>();
    private readonly INotificationRecipientRepository _recipients = Substitute.For<INotificationRecipientRepository>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IForexService _forex = Substitute.For<IForexService>();
    private readonly IMarketRegimeService _regime = Substitute.For<IMarketRegimeService>();
    private readonly IFinnhubRateLimiter _rateLimiter = Substitute.For<IFinnhubRateLimiter>();
    private readonly IFinnhubClient _finnhub = Substitute.For<IFinnhubClient>();
    private readonly ITiingoClient _tiingo = Substitute.For<ITiingoClient>();
    private readonly ITrading212Client _t212 = Substitute.For<ITrading212Client>();

    private ExecutionService CreateSut() => new(
        _signalRepo, _tradeRepo, _portfolioRepo, _approvalRepo, _accountRepo, _sizing, _riskProfileRepo,
        _recipients, _email, new MemoryCache(new MemoryCacheOptions()), _forex, _regime, _rateLimiter,
        Options.Create(new ExecutionConfig()), NullLogger<ExecutionService>.Instance);

    private void SetupAccount(bool approvalRequired, TradingMode mode = TradingMode.Demo) =>
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = 1, ApprovalRequired = approvalRequired, TradingMode = mode });

    private static StockSignal BuySignal(string symbol, decimal conviction, bool wasExecuted = false) => new()
    {
        Symbol = symbol,
        Recommendation = Recommendation.Buy,
        ConvictionScore = conviction,
        WasExecuted = wasExecuted,
        CurrentPrice = 100m,
    };

    [Fact]
    public async Task RunAsync_ApprovalRequiredButNoApprovalRow_SkipsExecution()
    {
        SetupAccount(approvalRequired: true);
        _approvalRepo.GetByDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>()).Returns((TradeApproval?)null);

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.OrdersPlaced.Should().Be(0);
        result.Summary.Should().Be("No approval for today");
        await _signalRepo.DidNotReceive().GetByDateAsync(Arg.Any<int>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task RunAsync_ApprovalExistsButNotApproved_SkipsExecution()
    {
        SetupAccount(approvalRequired: true);
        _approvalRepo.GetByDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>())
            .Returns(new TradeApproval { IsApproved = false });

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.Summary.Should().Be("No approval for today");
    }

    [Fact]
    public async Task RunAsync_ApprovalNotRequired_ProceedsWithoutCheckingApprovalRow()
    {
        SetupAccount(approvalRequired: false);
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new AccountRiskProfile());
        _tradeRepo.GetClosedOnDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>()).Returns([]);
        _signalRepo.GetByDateAsync(1, Arg.Any<DateOnly>()).Returns([]); // no signals -> short-circuits before the T212 delay

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.Summary.Should().Be("No eligible signals");
        await _approvalRepo.DidNotReceive().GetByDateAsync(Arg.Any<int>(), Arg.Any<TradingMode>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task RunAsync_ApprovedSymbolsExcludesAllEligibleSignals_ReturnsNoEligibleSignals()
    {
        // ApprovedSymbols scopes which of the day's Buy signals may execute -
        // approving only a symbol that isn't actually an eligible signal
        // today must leave nothing to trade, without ever reaching the T212
        // calls further down (which is what makes this assertable quickly).
        SetupAccount(approvalRequired: true);
        _approvalRepo.GetByDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>())
            .Returns(new TradeApproval { IsApproved = true, ApprovedSymbols = "GOOG" });
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new AccountRiskProfile());
        _tradeRepo.GetClosedOnDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>()).Returns([]);
        _signalRepo.GetByDateAsync(1, Arg.Any<DateOnly>()).Returns([BuySignal("AAPL", 8m), BuySignal("MSFT", 9m)]);

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.Summary.Should().Be("No eligible signals");
    }

    [Fact]
    public async Task RunAsync_AllSignalsAlreadyExecutedToday_ReturnsNoEligibleSignals()
    {
        SetupAccount(approvalRequired: false);
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new AccountRiskProfile());
        _tradeRepo.GetClosedOnDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>()).Returns([]);
        _signalRepo.GetByDateAsync(1, Arg.Any<DateOnly>()).Returns([BuySignal("AAPL", 8m, wasExecuted: true)]);

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.Summary.Should().Be("No eligible signals");
    }

    [Fact]
    public async Task RunAsync_SymbolClosedEarlierToday_IsExcludedFromEligibleSignals()
    {
        // The same-day re-buy guard: a symbol whose position closed today
        // must not be immediately re-bought even though its signal is still
        // sitting there as an unexecuted Buy.
        SetupAccount(approvalRequired: false);
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new AccountRiskProfile());
        _tradeRepo.GetClosedOnDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>())
            .Returns([new Trade { Symbol = "AAPL", Status = TradeStatus.Closed }]);
        _signalRepo.GetByDateAsync(1, Arg.Any<DateOnly>()).Returns([BuySignal("AAPL", 8m)]);

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.Summary.Should().Be("No eligible signals");
    }

    [Fact]
    public async Task RunAsync_NonBuyRecommendations_AreExcludedFromEligibleSignals()
    {
        SetupAccount(approvalRequired: false);
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new AccountRiskProfile());
        _tradeRepo.GetClosedOnDateAsync(1, TradingMode.Demo, Arg.Any<DateOnly>()).Returns([]);
        _signalRepo.GetByDateAsync(1, Arg.Any<DateOnly>()).Returns(
        [
            new StockSignal { Symbol = "AAPL", Recommendation = Recommendation.Watch, ConvictionScore = 9m },
            new StockSignal { Symbol = "MSFT", Recommendation = Recommendation.Hold, ConvictionScore = 9m },
            new StockSignal { Symbol = "TSLA", Recommendation = Recommendation.Avoid, ConvictionScore = 9m },
        ]);

        var result = await CreateSut().RunAsync(1, _finnhub, _tiingo, _t212, DateOnly.FromDateTime(DateTime.UtcNow));

        result.Summary.Should().Be("No eligible signals");
        result.SignalsSkipped.Should().Be(0);
    }
}
