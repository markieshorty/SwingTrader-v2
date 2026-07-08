using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.Watchlist;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using Xunit;

namespace SwingTrader.Tests;

public class StockScreenerTests
{
    private readonly IFinnhubRateLimiter _rateLimiter = Substitute.For<IFinnhubRateLimiter>();
    private readonly IWatchlistRepository _watchlist = Substitute.For<IWatchlistRepository>();
    private readonly ITradeRepository _trades = Substitute.For<ITradeRepository>();
    private readonly IAccountRepository _accountRepo = Substitute.For<IAccountRepository>();
    private readonly IMarketUniverseService _universe = Substitute.For<IMarketUniverseService>();
    private readonly IFinnhubClient _finnhub = Substitute.For<IFinnhubClient>();

    private StockScreener CreateSut(WatchlistConfig cfg, bool topMoversEnabled = false)
    {
        _watchlist.GetActiveAsync(1).Returns(new List<WatchlistItem>());
        _watchlist.IsTopMoversEnabledAsync(1, Arg.Any<CancellationToken>()).Returns(topMoversEnabled);
        _accountRepo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new Account { Id = 1, TradingMode = TradingMode.Demo });
        _trades.GetOpenTradesAsync(1, TradingMode.Demo).Returns(new List<Trade>());
        return new StockScreener(_rateLimiter, _watchlist, _trades, _accountRepo, _universe, Options.Create(cfg), NullLogger<StockScreener>.Instance);
    }

    private static WatchlistConfig DefaultConfig() => new()
    {
        MinPrice = 1m,
        MaxPrice = 10000m,
        MinAbsChangePercent = 0m,
        MaxAbsChangePercent = 100m,
        MaxCandidatesForClaude = 80,
    };

    private void SetupUniverse(params string[] symbols)
    {
        _universe.GetUniverseAsync(Arg.Any<CancellationToken>()).Returns(symbols.ToList());
        foreach (var s in symbols)
            _finnhub.GetQuoteAsync(s).Returns(new FinnhubQuoteResponse(100m, 1m, 1m, 101m, 99m, 99m, 99m, 0));
    }

    [Fact]
    public async Task ScreenAsync_TopMoversDisabled_NeverCallsMoverEndpoints()
    {
        var cfg = DefaultConfig();
        SetupUniverse("AAA");
        var sut = CreateSut(cfg, topMoversEnabled: false);

        await sut.ScreenAsync(1, _finnhub);

        await _finnhub.DidNotReceive().GetTopGainersAsync();
        await _finnhub.DidNotReceive().GetTopLosersAsync();
        await _finnhub.DidNotReceive().GetMostActiveAsync();
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_AddsNewMoverNotInIndexUniverse()
    {
        var cfg = DefaultConfig();
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 5m, 8m, 1_000_000) });
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg, topMoversEnabled: true);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Candidates.Should().Contain(c => c.Symbol == "ZZZ" && c.IsTopMover);
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_UpgradesExistingCandidateInsteadOfDuplicating()
    {
        var cfg = DefaultConfig();
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("AAA", "Aaa Corp", 12m, 3m, 6m, 500_000) });
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg, topMoversEnabled: true);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Candidates.Count(c => c.Symbol == "AAA").Should().Be(1);
        results.Candidates.Single(c => c.Symbol == "AAA").IsTopMover.Should().BeTrue();
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_FiltersOutMoversFailingChangeThreshold()
    {
        var cfg = DefaultConfig();
        cfg.MinAbsChangePercent = 5m;
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 1m, 1m, 1_000_000) }); // 1% < 5% min
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg, topMoversEnabled: true);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Candidates.Should().NotContain(c => c.Symbol == "ZZZ");
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_ExcludesSymbolsAlreadyOnActiveWatchlist()
    {
        var cfg = DefaultConfig();
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 5m, 8m, 1_000_000) });
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg, topMoversEnabled: true);
        _watchlist.GetActiveAsync(1).Returns(new List<WatchlistItem> { new() { Symbol = "ZZZ" } });

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Candidates.Should().NotContain(c => c.Symbol == "ZZZ");
    }

    [Fact]
    public async Task ScreenAsync_TopMoversFetchThrows_StillReturnsIndexUniverseCandidates()
    {
        var cfg = DefaultConfig();
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns<List<MarketMoverItem>>(_ => throw new HttpRequestException("boom"));
        var sut = CreateSut(cfg, topMoversEnabled: true);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Candidates.Should().Contain(c => c.Symbol == "AAA");
    }

    [Fact]
    public async Task ScreenAsync_QuoteFetchFailures_AreCountedInResultNotFatal()
    {
        var cfg = DefaultConfig();
        SetupUniverse("AAA", "BBB", "CCC");
        _finnhub.GetQuoteAsync("BBB").Returns<FinnhubQuoteResponse>(_ => throw new HttpRequestException("boom"));
        var sut = CreateSut(cfg);

        var result = await sut.ScreenAsync(1, _finnhub);

        result.UniverseCount.Should().Be(3);
        result.FailedQuoteCount.Should().Be(1);
        result.Candidates.Should().Contain(c => c.Symbol == "AAA")
            .And.Contain(c => c.Symbol == "CCC");
        result.Candidates.Should().NotContain(c => c.Symbol == "BBB");
    }

    [Fact]
    public async Task ScreenAsync_TopMoverBoost_CanOutrankALargerNonMoverWhenTruncated()
    {
        var cfg = DefaultConfig();
        cfg.MaxCandidatesForClaude = 1;
        cfg.TopMoverOrderBoost = 3m;
        SetupUniverse("AAA"); // AAA quote change = 1% via SetupUniverse's fixed quote
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 2m, 2m, 1_000_000) }); // 2% * 3 boost = 6 > AAA's 1%
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg, topMoversEnabled: true);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Candidates.Should().ContainSingle();
        results.Candidates[0].Symbol.Should().Be("ZZZ");
    }
}
