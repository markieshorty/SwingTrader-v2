using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.Risk;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

public class TierEvaluationServiceTests
{
    private readonly ITradeRepository _tradeRepo = Substitute.For<ITradeRepository>();
    private readonly IPortfolioRepository _portfolioRepo = Substitute.For<IPortfolioRepository>();
    private readonly ITierEvaluationRepository _evaluationRepo = Substitute.For<ITierEvaluationRepository>();
    private readonly IAccountRiskProfileRepository _riskProfileRepo = Substitute.For<IAccountRiskProfileRepository>();
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly IIndicatorService _indicators = Substitute.For<IIndicatorService>();
    private readonly INotificationRecipientRepository _recipients = Substitute.For<INotificationRecipientRepository>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IClaudeClient _claude = Substitute.For<IClaudeClient>();
    private readonly TierEvaluationService _sut;

    public TierEvaluationServiceTests()
    {
        _evaluationRepo.AddAsync(Arg.Any<TierEvaluationRecord>()).Returns(ci => ci.Arg<TierEvaluationRecord>());
        _recipients.ListAsync(Arg.Any<int>()).Returns(new List<NotificationRecipient>());
        _claude.SendMessageAsync(Arg.Any<ClaudeRequest>()).Returns<Task<ClaudeResponse>>(_ => throw new Exception("no claude in tests"));
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Account { Id = 1, TradingMode = TradingMode.Demo });

        _sut = new TierEvaluationService(
            _tradeRepo, _portfolioRepo, _evaluationRepo, _riskProfileRepo, _accountRepo, _indicators,
            _recipients, _email, Substitute.For<IClaudeRateLimiter>(),
            Options.Create(new RiskManagementConfig { Active = true }),
            Options.Create(new ClaudeConfig()),
            NullLogger<TierEvaluationService>.Instance);
    }

    private static Trade ClosedTrade(decimal pnl, decimal entryPrice = 100m, decimal quantity = 10m) => new()
    {
        Symbol = "AAA",
        EntryPrice = entryPrice,
        Quantity = quantity,
        Status = TradeStatus.Closed,
        ClosedAt = DateTime.UtcNow.AddDays(-1),
        RealizedPnl = pnl,
    };

    private void SetupTrades(int wins, int losses, CapitalTier currentTier)
    {
        var trades = new List<Trade>();
        trades.AddRange(Enumerable.Range(0, wins).Select(_ => ClosedTrade(50m)));
        trades.AddRange(Enumerable.Range(0, losses).Select(_ => ClosedTrade(-10m)));
        _tradeRepo.GetTradeHistoryAsync(1, TradingMode.Demo, Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(trades);
        _portfolioRepo.GetLatestSnapshotAsync(1, TradingMode.Demo).Returns(new PortfolioSnapshot { CurrentTier = currentTier });
    }

    [Fact]
    public async Task EvaluateAsync_MeetsProfileTier1Thresholds_SuggestsTier2()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { Tier1UnlockMinTrades = 5, Tier1UnlockMinWinRate = 0.5m });
        SetupTrades(wins: 4, losses: 1, currentTier: CapitalTier.Tier1); // 5 trades, 80% win rate

        var record = await _sut.EvaluateAsync(1, _claude);

        record.UnlockCriteriaMet.Should().BeTrue();
        record.SuggestedTier.Should().Be(CapitalTier.Tier2);
    }

    [Fact]
    public async Task EvaluateAsync_BelowProfileTier1MinTrades_StaysOnTier1()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { Tier1UnlockMinTrades = 30, Tier1UnlockMinWinRate = 0.5m });
        SetupTrades(wins: 4, losses: 1, currentTier: CapitalTier.Tier1); // only 5 trades

        var record = await _sut.EvaluateAsync(1, _claude);

        record.UnlockCriteriaMet.Should().BeFalse();
        record.SuggestedTier.Should().Be(CapitalTier.Tier1);
    }

    [Fact]
    public async Task EvaluateAsync_MeetsProfileTier2Thresholds_SuggestsTier3()
    {
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { Tier2UnlockMinTrades = 5, Tier2UnlockMinWinRate = 0.5m });
        SetupTrades(wins: 4, losses: 1, currentTier: CapitalTier.Tier2);

        var record = await _sut.EvaluateAsync(1, _claude);

        record.UnlockCriteriaMet.Should().BeTrue();
        record.SuggestedTier.Should().Be(CapitalTier.Tier3);
    }

    [Fact]
    public async Task EvaluateAsync_StricterProfileThanDefault_DoesNotUnlockWhereDefaultWould()
    {
        // 10 trades, 60% win rate - would clear the static CapitalRules default (30 trades)
        // requirement only if far more trades existed, but check a tighter custom profile here.
        _riskProfileRepo.GetAsync(1, Arg.Any<CancellationToken>())
            .Returns(new AccountRiskProfile { Tier1UnlockMinTrades = 50, Tier1UnlockMinWinRate = 0.5m });
        SetupTrades(wins: 6, losses: 4, currentTier: CapitalTier.Tier1);

        var record = await _sut.EvaluateAsync(1, _claude);

        record.UnlockCriteriaMet.Should().BeFalse();
        record.SuggestedTier.Should().Be(CapitalTier.Tier1);
    }
}
