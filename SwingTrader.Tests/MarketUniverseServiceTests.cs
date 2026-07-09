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

    public MarketUniverseServiceTests()
    {
        // Default every index lookup to empty so each test only configures the
        // ones it cares about (the universe now draws from four lists, not two).
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _wikipedia.GetSp400ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _wikipedia.GetSp600ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    private MarketUniverseService CreateSut(IMemoryCache cache, WatchlistConfig? config = null) =>
        new(cache, _wikipedia, Options.Create(config ?? new WatchlistConfig()), NullLogger<MarketUniverseService>.Instance);

    // Wrap plain tickers as UniverseSymbol (name unused in most assertions).
    private static List<UniverseSymbol> Us(params string[] symbols) =>
        symbols.Select(s => new UniverseSymbol(s, $"{s} Inc.")).ToList();

    private static List<UniverseSymbol> UsRange(int start, int count) =>
        Enumerable.Range(start, count).Select(i => new UniverseSymbol(FakeSymbol(i), $"Company {i}")).ToList();

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
    public async Task GetUniverse_FetchesLargeAndNasdaq()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(UsRange(0, 503));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(UsRange(1000, 101));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().Contain(FakeSymbol(0)).And.Contain(FakeSymbol(1000));
        result.Should().HaveCount(604);
    }

    [Fact]
    public async Task GetUniverse_IncludesMidAndSmallCaps()
    {
        // The whole point of Lever 1: mid (S&P 400) and small (S&P 600) caps
        // must land in the universe alongside the large caps.
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("AAPL"));
        _wikipedia.GetSp400ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("MIDA", "MIDB"));
        _wikipedia.GetSp600ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("SMLA", "SMLB"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("NVDA"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().Contain(["AAPL", "MIDA", "MIDB", "SMLA", "SMLB", "NVDA"]);
        result.Should().HaveCount(6);
    }

    [Fact]
    public async Task GetUniverseWithNames_ReturnsCompanyNames()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns([new UniverseSymbol("AAPL", "Apple Inc.")]);
        _wikipedia.GetSp400ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns([new UniverseSymbol("WING", "Wingstop Inc.")]);

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseWithNamesAsync();

        result.Should().ContainEquivalentOf(new UniverseSymbol("AAPL", "Apple Inc."));
        result.Should().ContainEquivalentOf(new UniverseSymbol("WING", "Wingstop Inc."));
    }

    [Fact]
    public async Task GetUniverse_Deduplicates()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("AAPL", "MSFT", "SPXO"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("AAPL", "MSFT", "NDXO"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Count(s => s.Equals("AAPL", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Count(s => s.Equals("MSFT", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetUniverse_CachedForConfiguredDays()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("AAPL"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("MSFT"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));

        await sut.GetUniverseAsync();
        await sut.GetUniverseAsync();

        await _wikipedia.Received(1).GetSp500ConstituentsAsync(Arg.Any<CancellationToken>());
        await _wikipedia.Received(1).GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUniverse_InvalidSymbolsExcluded()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(Us("BRK.B", "BF-B", "AAPL"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().NotContain("BRK.B").And.NotContain("BF-B");
        result.Should().Contain("AAPL");
    }

    [Fact]
    public async Task GetUniverse_OneFails_OtherSucceeds()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<UniverseSymbol>>>(_ => throw new HttpRequestException("boom"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>()).Returns(UsRange(0, 101));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().HaveCount(101);
        result.Should().Contain(FakeSymbol(0));
    }

    [Fact]
    public async Task GetUniverse_AllFail_ReturnsEmpty()
    {
        _wikipedia.GetSp500ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<UniverseSymbol>>>(_ => throw new HttpRequestException("boom"));
        _wikipedia.GetNasdaq100ConstituentsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<UniverseSymbol>>>(_ => throw new HttpRequestException("boom"));

        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUniverse_EmptyResultsFromAll_ReturnsEmpty()
    {
        var sut = CreateSut(new MemoryCache(new MemoryCacheOptions()));
        var result = await sut.GetUniverseAsync();

        result.Should().BeEmpty();
    }
}
