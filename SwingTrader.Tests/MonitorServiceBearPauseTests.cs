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
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

// Regime autopause: each monitor cycle detects the market regime, and the
// active regime's risk book decides whether new entries pause. It auto-resumes
// when the regime moves to a book that permits entries, and never touches
// pauses it doesn't own (manual/circuit-breaker). By default the Bear and
// Crisis books auto-pause; Bull and Neutral don't.
public class MonitorServiceBearPauseTests
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
    private readonly ITiingoClient _tiingo = Substitute.For<ITiingoClient>();
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly IActivityLogRepository _activityLog = Substitute.For<IActivityLogRepository>();
    private readonly IMarketRegimeService _regime = Substitute.For<IMarketRegimeService>();

    private readonly Account _account = new() { Id = 1, TradingMode = TradingMode.Demo };

    public MonitorServiceBearPauseTests()
    {
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_account);
        _t212.GetAccountSummaryAsync().Returns(new T212AccountSummary(
            1000m,
            new T212AccountSummaryCash(1000m, 0m, 0m),
            new T212AccountSummaryInvestments(0m, 0m, 0m, 0m)));
        _circuitBreaker.ShouldTriggerAsync(1, Arg.Any<T212AccountSummary?>(), Arg.Any<CancellationToken>()).Returns(false);
        // The active-book resolver (used by position monitoring, step 2).
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { AccountId = 1, Regime = MarketRegime.Neutral });
        // Active book is resolved per regime: the default posture auto-pauses in
        // Bear/Crisis and permits entries in Bull/Neutral.
        _riskProfileRepo.GetAsync(1, Arg.Any<MarketRegime>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AccountRiskProfile
            {
                AccountId = 1,
                Regime = ci.Arg<MarketRegime>(),
                AutopauseTrading = ci.Arg<MarketRegime>() is MarketRegime.Bear or MarketRegime.Crisis,
            });
        _tradeRepo.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>());
        _tradeRepo.GetUnreconciledOrdersAsync(1, TradingMode.Demo).Returns(new List<Trade>());
    }

    private MonitorService CreateSut() => new(
        _tradeRepo, _portfolioRepo, _circuitBreaker, _positionMonitor, _riskProfileRepo,
        _positionExit, _recipients, _emailService, _accountRepo, _activityLog, _regime,
        Substitute.For<IFilingRepository>(),
        Options.Create(new ExecutionConfig { DelayBetweenOrdersSeconds = 0 }),
        Options.Create(new SwingTrader.Infrastructure.Configuration.FilingDeltaConfig()),
        Substitute.For<ISignalRepository>(),
        Substitute.For<IJobLogRepository>(),
        NullLogger<MonitorService>.Instance);

    private void SetupRegime(MarketRegime regime) =>
        _regime.GetCurrentRegimeAsync(_tiingo, _finnhub, Arg.Any<CancellationToken>())
            .Returns(new MarketRegimeResult(regime, 100m, 100m, 100m, 0m, regime.ToString()));

    [Fact]
    public async Task DefensiveRegime_BookPausesOn_PausesEntriesWithRegimeReason()
    {
        SetupRegime(MarketRegime.Bear);

        await CreateSut().RunCycleAsync(1, _finnhub, _t212, _tiingo);

        _account.IsExecutionPaused(TradingMode.Demo).Should().BeTrue();
        _account.ExecutionPauseReasonFor(TradingMode.Demo).Should().Be(ExecutionPauseReason.RegimeAutopause);
        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Entries Auto-Paused", "Warning", Arg.Any<string>());
    }

    [Fact]
    public async Task DefensiveRegime_BookPausesOff_DoesNotPause()
    {
        // This account's Bear book permits entries.
        _riskProfileRepo.GetAsync(1, MarketRegime.Bear, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { AccountId = 1, Regime = MarketRegime.Bear, AutopauseTrading = false });
        SetupRegime(MarketRegime.Bear);

        await CreateSut().RunCycleAsync(1, _finnhub, _t212, _tiingo);

        _account.IsExecutionPaused(TradingMode.Demo).Should().BeFalse();
    }

    [Fact]
    public async Task RegimePaused_MovesToPermittingRegime_AutoResumes()
    {
        _account.PauseExecution(TradingMode.Demo, ExecutionPauseReason.RegimeAutopause, DateTime.UtcNow);
        SetupRegime(MarketRegime.Bull);

        await CreateSut().RunCycleAsync(1, _finnhub, _t212, _tiingo);

        _account.IsExecutionPaused(TradingMode.Demo).Should().BeFalse();
        await _activityLog.Received(1).LogAsync(1, "SystemEvent", "Entries Auto-Resumed", "Info", Arg.Any<string>());
    }

    [Fact]
    public async Task RegimePaused_StillDefensive_StaysPausedWithoutDuplicateAlerts()
    {
        _account.PauseExecution(TradingMode.Demo, ExecutionPauseReason.RegimeAutopause, DateTime.UtcNow);
        SetupRegime(MarketRegime.Bear);

        await CreateSut().RunCycleAsync(1, _finnhub, _t212, _tiingo);

        _account.IsExecutionPaused(TradingMode.Demo).Should().BeTrue();
        await _activityLog.DidNotReceive().LogAsync(1, "SystemEvent", "Entries Auto-Paused", Arg.Any<string>(), Arg.Any<string>());
        await _activityLog.DidNotReceive().LogAsync(1, "SystemEvent", "Entries Auto-Resumed", Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ManualOrCircuitBreakerPause_NeverAutoResumed()
    {
        _account.PauseExecution(TradingMode.Demo, ExecutionPauseReason.CircuitBreaker, DateTime.UtcNow);
        SetupRegime(MarketRegime.Bull);

        await CreateSut().RunCycleAsync(1, _finnhub, _t212, _tiingo);

        _account.IsExecutionPaused(TradingMode.Demo).Should().BeTrue();
        _account.ExecutionPauseReasonFor(TradingMode.Demo).Should().Be(ExecutionPauseReason.CircuitBreaker);
    }

    [Fact]
    public async Task NoTiingoClient_RegimeCheckSkippedEntirely()
    {
        SetupRegime(MarketRegime.Bear);

        await CreateSut().RunCycleAsync(1, _finnhub, _t212, tiingo: null);

        _account.IsExecutionPaused(TradingMode.Demo).Should().BeFalse();
        await _regime.DidNotReceive().GetCurrentRegimeAsync(Arg.Any<ITiingoClient>(), Arg.Any<IFinnhubClient>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegimeFetchFails_CycleStillCompletes()
    {
        _regime.GetCurrentRegimeAsync(_tiingo, _finnhub, Arg.Any<CancellationToken>())
            .Returns<MarketRegimeResult>(_ => throw new HttpRequestException("tiingo down"));

        var act = () => CreateSut().RunCycleAsync(1, _finnhub, _t212, _tiingo);

        await act.Should().NotThrowAsync();
        _account.IsExecutionPaused(TradingMode.Demo).Should().BeFalse();
    }
}
