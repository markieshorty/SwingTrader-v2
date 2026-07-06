using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using Xunit;

namespace SwingTrader.Tests;

public class PortfolioCircuitBreakerServiceTests
{
    private readonly IPortfolioRepository _portfolioRepo = Substitute.For<IPortfolioRepository>();
    private readonly IAccountRiskProfileRepository _riskProfileRepo = Substitute.For<IAccountRiskProfileRepository>();
    private readonly ITrading212Client _t212 = Substitute.For<ITrading212Client>();
    private readonly PortfolioCircuitBreakerService _sut;

    public PortfolioCircuitBreakerServiceTests()
    {
        _sut = new PortfolioCircuitBreakerService(_portfolioRepo, _riskProfileRepo, NullLogger<PortfolioCircuitBreakerService>.Instance);
    }

    private void SetupBaseline(decimal totalCapital)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _portfolioRepo.GetSnapshotHistoryAsync(1, today, today)
            .Returns(new List<PortfolioSnapshot> { new() { TotalCapital = totalCapital, CreatedAt = DateTime.UtcNow.AddHours(-1) } });
    }

    private void SetupLiveValue(decimal cash, decimal positionsValue)
    {
        _t212.GetAccountSummaryAsync().Returns(new T212AccountSummary(
            new T212AccountSummaryCash(cash, cash, 0, 0, 0, 0, cash)));
        _t212.GetPortfolioAsync().Returns(new List<PortfolioPositionResponse>
        {
            new("AAA_US_EQ", 1, 100, positionsValue, 0, null, null, null, null, null, null)
        });
    }

    [Fact]
    public async Task ShouldTriggerAsync_DrawdownBelowProfileThreshold_ReturnsFalse()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.10m });
        SetupBaseline(10000m);
        SetupLiveValue(cash: 0, positionsValue: 9500m); // 5% down

        var triggered = await _sut.ShouldTriggerAsync(1, _t212);

        triggered.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldTriggerAsync_DrawdownExceedsProfileThreshold_ReturnsTrue()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.05m });
        SetupBaseline(10000m);
        SetupLiveValue(cash: 0, positionsValue: 9000m); // 10% down

        var triggered = await _sut.ShouldTriggerAsync(1, _t212);

        triggered.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldTriggerAsync_SameDrawdown_TighterProfileTriggersLooserDoesNot()
    {
        SetupBaseline(10000m);
        SetupLiveValue(cash: 0, positionsValue: 9300m); // 7% down

        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.05m });
        var tighterTriggered = await _sut.ShouldTriggerAsync(1, _t212);

        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { DailyLossCircuitBreakerPct = 0.15m });
        var looserTriggered = await _sut.ShouldTriggerAsync(1, _t212);

        tighterTriggered.Should().BeTrue();
        looserTriggered.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldTriggerAsync_NoBaselineSnapshot_ReturnsFalse()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _portfolioRepo.GetSnapshotHistoryAsync(1, today, today).Returns(new List<PortfolioSnapshot>());

        var triggered = await _sut.ShouldTriggerAsync(1, _t212);

        triggered.Should().BeFalse();
    }
}
