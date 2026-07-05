using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class MarketUniverseServiceTests
{
    private static MarketUniverseService CreateSut(IMemoryCache cache, WatchlistConfig? config = null) =>
        new(cache, Options.Create(config ?? new WatchlistConfig()), NullLogger<MarketUniverseService>.Instance);

    // IsValidSymbol requires letters only (real tickers never contain digits) -
    // this generates distinct letters-only fake tickers for bulk test data,
    // like Excel column naming (A, B, ... Z, AA, AB, ...).
    private static string FakeSymbol(int index)
    {
        var chars = new List<char>();
        var n = index;
        do
        {
            chars.Insert(0, (char)('A' + (n % 26)));
            n = n / 26 - 1;
        } while (n >= 0);
        return new string(chars.ToArray());
    }

    [Fact]
    public async Task GetUniverse_FetchesBothIndices()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns(new IndexConstituentsResponse(
            Enumerable.Range(0, 503).Select(FakeSymbol).ToList(), "^GSPC"));
        finnhub.GetIndexConstituentsAsync("^NDX").Returns(new IndexConstituentsResponse(
            Enumerable.Range(1000, 101).Select(FakeSymbol).ToList(), "^NDX"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync(finnhub);

        result.Should().Contain(FakeSymbol(0)).And.Contain(FakeSymbol(1000));
        result.Should().HaveCount(604);
    }

    [Fact]
    public async Task GetUniverse_Deduplicates()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns(new IndexConstituentsResponse(["AAPL", "MSFT", "SPXO"], "^GSPC"));
        finnhub.GetIndexConstituentsAsync("^NDX").Returns(new IndexConstituentsResponse(["AAPL", "MSFT", "NDXO"], "^NDX"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync(finnhub);

        result.Count(s => s.Equals("AAPL", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Count(s => s.Equals("MSFT", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetUniverse_CachedForConfiguredDays()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns(new IndexConstituentsResponse(["AAPL"], "^GSPC"));
        finnhub.GetIndexConstituentsAsync("^NDX").Returns(new IndexConstituentsResponse(["MSFT"], "^NDX"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));

        await sut.GetUniverseAsync(finnhub);
        await sut.GetUniverseAsync(finnhub);

        await finnhub.Received(1).GetIndexConstituentsAsync("^GSPC");
        await finnhub.Received(1).GetIndexConstituentsAsync("^NDX");
    }

    [Fact]
    public async Task GetUniverse_InvalidSymbolsExcluded()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns(new IndexConstituentsResponse(["BRK.B", "BF-B", "AAPL"], "^GSPC"));
        finnhub.GetIndexConstituentsAsync("^NDX").Returns(new IndexConstituentsResponse([], "^NDX"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync(finnhub);

        result.Should().NotContain("BRK.B").And.NotContain("BF-B");
        result.Should().Contain("AAPL");
    }

    [Fact]
    public async Task GetUniverse_OneFails_OtherSucceeds()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns<Task<IndexConstituentsResponse>>(_ => throw new HttpRequestException("boom"));
        finnhub.GetIndexConstituentsAsync("^NDX").Returns(new IndexConstituentsResponse(
            Enumerable.Range(0, 101).Select(FakeSymbol).ToList(), "^NDX"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync(finnhub);

        result.Should().HaveCount(101);
        result.Should().Contain(FakeSymbol(0));
    }

    [Fact]
    public async Task GetUniverse_BothFail_ReturnsEmpty()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns<Task<IndexConstituentsResponse>>(_ => throw new HttpRequestException("boom"));
        finnhub.GetIndexConstituentsAsync("^NDX").Returns<Task<IndexConstituentsResponse>>(_ => throw new HttpRequestException("boom"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync(finnhub);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUniverse_RespectsConfiguredIndexList()
    {
        var finnhub = Substitute.For<IFinnhubClient>();
        finnhub.GetIndexConstituentsAsync("^GSPC").Returns(new IndexConstituentsResponse(["AAPL"], "^GSPC"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()), new WatchlistConfig { IndexSymbols = ["^GSPC"] });
        var result = await sut.GetUniverseAsync(finnhub);

        result.Should().ContainSingle().Which.Should().Be("AAPL");
        await finnhub.DidNotReceive().GetIndexConstituentsAsync("^NDX");
    }
}
