using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.Watchlist;
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
    private readonly IRateLimiter _rateLimiter = Substitute.For<IRateLimiter>();
    private readonly IWatchlistRepository _watchlist = Substitute.For<IWatchlistRepository>();
    private readonly ITradeRepository _trades = Substitute.For<ITradeRepository>();
    private readonly IMarketUniverseService _universe = Substitute.For<IMarketUniverseService>();
    private readonly IFinnhubClient _finnhub = Substitute.For<IFinnhubClient>();

    private StockScreener CreateSut(WatchlistConfig cfg)
    {
        _watchlist.GetActiveAsync(1).Returns(new List<WatchlistItem>());
        _trades.GetOpenTradesAsync(1).Returns(new List<Trade>());
        return new StockScreener(_rateLimiter, _watchlist, _trades, _universe, Options.Create(cfg), NullLogger<StockScreener>.Instance);
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
        _universe.GetUniverseAsync(_finnhub, Arg.Any<CancellationToken>()).Returns(symbols.ToList());
        foreach (var s in symbols)
            _finnhub.GetQuoteAsync(s).Returns(new FinnhubQuoteResponse(100m, 1m, 1m, 101m, 99m, 99m, 99m, 0));
    }

    [Fact]
    public async Task ScreenAsync_TopMoversDisabled_NeverCallsMoverEndpoints()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = false;
        SetupUniverse("AAA");
        var sut = CreateSut(cfg);

        await sut.ScreenAsync(1, _finnhub);

        await _finnhub.DidNotReceive().GetTopGainersAsync();
        await _finnhub.DidNotReceive().GetTopLosersAsync();
        await _finnhub.DidNotReceive().GetMostActiveAsync();
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_AddsNewMoverNotInIndexUniverse()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = true;
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 5m, 8m, 1_000_000) });
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Should().Contain(c => c.Symbol == "ZZZ" && c.IsTopMover);
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_UpgradesExistingCandidateInsteadOfDuplicating()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = true;
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("AAA", "Aaa Corp", 12m, 3m, 6m, 500_000) });
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Count(c => c.Symbol == "AAA").Should().Be(1);
        results.Single(c => c.Symbol == "AAA").IsTopMover.Should().BeTrue();
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_FiltersOutMoversFailingChangeThreshold()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = true;
        cfg.MinAbsChangePercent = 5m;
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 1m, 1m, 1_000_000) }); // 1% < 5% min
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Should().NotContain(c => c.Symbol == "ZZZ");
    }

    [Fact]
    public async Task ScreenAsync_TopMoversEnabled_ExcludesSymbolsAlreadyOnActiveWatchlist()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = true;
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 5m, 8m, 1_000_000) });
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg);
        _watchlist.GetActiveAsync(1).Returns(new List<WatchlistItem> { new() { Symbol = "ZZZ" } });

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Should().NotContain(c => c.Symbol == "ZZZ");
    }

    [Fact]
    public async Task ScreenAsync_TopMoversFetchThrows_StillReturnsIndexUniverseCandidates()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = true;
        SetupUniverse("AAA");
        _finnhub.GetTopGainersAsync().Returns<List<MarketMoverItem>>(_ => throw new HttpRequestException("boom"));
        var sut = CreateSut(cfg);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Should().Contain(c => c.Symbol == "AAA");
    }

    [Fact]
    public async Task ScreenAsync_TopMoverBoost_CanOutrankALargerNonMoverWhenTruncated()
    {
        var cfg = DefaultConfig();
        cfg.TopMoversEnabled = true;
        cfg.MaxCandidatesForClaude = 1;
        cfg.TopMoverOrderBoost = 3m;
        SetupUniverse("AAA"); // AAA quote change = 1% via SetupUniverse's fixed quote
        _finnhub.GetTopGainersAsync().Returns(new List<MarketMoverItem> { new("ZZZ", "Zzz Corp", 50m, 2m, 2m, 1_000_000) }); // 2% * 3 boost = 6 > AAA's 1%
        _finnhub.GetTopLosersAsync().Returns(new List<MarketMoverItem>());
        _finnhub.GetMostActiveAsync().Returns(new List<MarketMoverItem>());
        var sut = CreateSut(cfg);

        var results = await sut.ScreenAsync(1, _finnhub);

        results.Should().ContainSingle();
        results[0].Symbol.Should().Be("ZZZ");
    }
}
