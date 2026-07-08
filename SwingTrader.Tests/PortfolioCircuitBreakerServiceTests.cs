using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using Xunit;

namespace SwingTrader.Tests;

public class PortfolioCircuitBreakerServiceTests
{
    private readonly IPortfolioRepository _portfolioRepo = Substitute.For<IPortfolioRepository>();
    private readonly IAccountRiskProfileRepository _riskProfileRepo = Substitute.For<IAccountRiskProfileRepository>();
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly PortfolioCircuitBreakerService _sut;

    public PortfolioCircuitBreakerServiceTests()
    {
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = 1, TradingMode = TradingMode.Demo });
        _sut = new PortfolioCircuitBreakerService(_portfolioRepo, _riskProfileRepo, _accountRepo, NullLogger<PortfolioCircuitBreakerService>.Instance);
    }

    private void SetupBaseline(decimal totalCapital)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _portfolioRepo.GetSnapshotHistoryAsync(1, TradingMode.Demo, today, today)
            .Returns(new List<PortfolioSnapshot> { new() { TotalCapital = totalCapital, CreatedAt = DateTime.UtcNow.AddHours(-1) } });
    }

    // ShouldTriggerAsync now takes an already-fetched summary directly
    // (MonitorService fetches it once per cycle and shares it with
    // UpdateSnapshotAsync, rather than each hitting T212 separately).
    private static T212AccountSummary BuildSummary(decimal cash, decimal positionsValue) =>
        new(cash + positionsValue,
            new T212AccountSummaryCash(cash, 0, 0),
            new T212AccountSummaryInvestments(positionsValue, positionsValue, 0, 0));

    [Fact]
    public async Task ShouldTriggerAsync_DrawdownBelowProfileThreshold_ReturnsFalse()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.10m });
        SetupBaseline(10000m);
        var summary = BuildSummary(cash: 0, positionsValue: 9500m); // 5% down

        var triggered = await _sut.ShouldTriggerAsync(1, summary);

        triggered.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldTriggerAsync_DrawdownExceedsProfileThreshold_ReturnsTrue()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.05m });
        SetupBaseline(10000m);
        var summary = BuildSummary(cash: 0, positionsValue: 9000m); // 10% down

        var triggered = await _sut.ShouldTriggerAsync(1, summary);

        triggered.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldTriggerAsync_SameDrawdown_TighterProfileTriggersLooserDoesNot()
    {
        SetupBaseline(10000m);
        var summary = BuildSummary(cash: 0, positionsValue: 9300m); // 7% down

        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.05m });
        var tighterTriggered = await _sut.ShouldTriggerAsync(1, summary);

        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.15m });
        var looserTriggered = await _sut.ShouldTriggerAsync(1, summary);

        tighterTriggered.Should().BeTrue();
        looserTriggered.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldTriggerAsync_NoBaselineSnapshot_ReturnsFalse()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _portfolioRepo.GetSnapshotHistoryAsync(1, TradingMode.Demo, today, today).Returns(new List<PortfolioSnapshot>());

        var triggered = await _sut.ShouldTriggerAsync(1, BuildSummary(0, 100));

        triggered.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldTriggerAsync_NullSummary_ReturnsFalse()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile());
        SetupBaseline(10000m);

        var triggered = await _sut.ShouldTriggerAsync(1, null);

        triggered.Should().BeFalse();
    }
}
