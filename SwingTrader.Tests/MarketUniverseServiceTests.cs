using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class MarketUniverseServiceTests
{
    private readonly IWikipediaIndexClient _wikipedia = Substitute.For<IWikipediaIndexClient>();

    private MarketUniverseService CreateSut(IMemoryCache cache, WatchlistConfig? config = null) =>
        new(cache, _wikipedia, Options.Create(config ?? new WatchlistConfig()), NullLogger<MarketUniverseService>.Instance);

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
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 503).Select(FakeSymbol).ToList());
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1000, 101).Select(FakeSymbol).ToList());

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().Contain(FakeSymbol(0)).And.Contain(FakeSymbol(1000));
        result.Should().HaveCount(604);
    }

    [Fact]
    public async Task GetUniverse_Deduplicates()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(["AAPL", "MSFT", "SPXO"]);
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(["AAPL", "MSFT", "NDXO"]);

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Count(s => s.Equals("AAPL", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Count(s => s.Equals("MSFT", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetUniverse_CachedForConfiguredDays()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(["AAPL"]);
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(["MSFT"]);

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));

        await sut.GetUniverseAsync();
        await sut.GetUniverseAsync();

        await _wikipedia.Received(1).GetSp500ConstituentsAsync(Arg.Any<CancellationToken>());
        await _wikipedia.Received(1).GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUniverse_InvalidSymbolsExcluded()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(["BRK.B", "BF-B", "AAPL"]);
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().NotContain("BRK.B").And.NotContain("BF-B");
        result.Should().Contain("AAPL");
    }

    [Fact]
    public async Task GetUniverse_OneFails_OtherSucceeds()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<string>>>(_ => throw new HttpRequestException("boom"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 101).Select(FakeSymbol).ToList());

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().HaveCount(101);
        result.Should().Contain(FakeSymbol(0));
    }

    [Fact]
    public async Task GetUniverse_BothFail_ReturnsEmpty()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<string>>>(_ => throw new HttpRequestException("boom"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<string>>>(_ => throw new HttpRequestException("boom"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUniverse_EmptyResultsFromBoth_ReturnsEmpty()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().BeEmpty();
    }
}
