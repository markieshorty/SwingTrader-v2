using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class RelativeStrengthServiceTests
{
    private readonly ICandleRepository _candleRepo = Substitute.For<ICandleRepository>();
    private readonly ITiingoClient _tiingo = Substitute.For<ITiingoClient>();
    // Empty sector map: every symbol falls through to the legacy
    // override-or-SPY path, preserving these tests' original expectations
    // (AAPL -> XLK via override, XYZQ -> SPY).
    private readonly IMarketUniverseService _universe = Substitute.For<IMarketUniverseService>();

    private RelativeStrengthService CreateSut(IMemoryCache? cache = null)
    {
        _universe.GetSectorEtfMapAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return new(_candleRepo, cache ?? new MemoryCache(new MemoryCacheOptions()), _universe,
            NullLogger<RelativeStrengthService>.Instance);
    }

    private static List<StockCandle> FlatCandles(int count, decimal close) =>
        Enumerable.Range(0, count)
            .Select(i => new StockCandle { Timestamp = DateTime.UtcNow.AddDays(i - count), Close = close })
            .ToList();

    private static List<TiingoDailyPrice> FlatEtfPrices(int count, decimal close) =>
        Enumerable.Range(0, count)
            .Select(i => new TiingoDailyPrice(DateTime.UtcNow.AddDays(i - count), close, close, close, close, 1000, close, close, close, close, 1000))
            .ToList();

    [Fact]
    public async Task CalculateAsync_InsufficientStockCandles_ReturnsNull()
    {
        _candleRepo.GetCandlesAsync("AAPL", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(FlatCandles(2, 100m));

        var result = await CreateSut().CalculateAsync(_tiingo, "AAPL", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_InsufficientEtfCandles_ReturnsNull()
    {
        _candleRepo.GetCandlesAsync("AAPL", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(FlatCandles(10, 100m));
        _tiingo.GetDailyPricesAsync("XLK", Arg.Any<string>(), Arg.Any<string>())
            .Returns(FlatEtfPrices(2, 100m));

        var result = await CreateSut().CalculateAsync(_tiingo, "AAPL", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_StockOutperformsEtf_ReturnsPositiveRelativeReturnAndSectorEtf()
    {
        // Stock rises from 100 to 110 over its last 5 bars (+10%); ETF stays flat.
        var stockCandles = FlatCandles(10, 100m);
        stockCandles[^1].Close = 110m;
        _candleRepo.GetCandlesAsync("AAPL", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(stockCandles);
        _tiingo.GetDailyPricesAsync("XLK", Arg.Any<string>(), Arg.Any<string>()).Returns(FlatEtfPrices(10, 100m));

        var result = await CreateSut().CalculateAsync(_tiingo, "AAPL", CancellationToken.None);

        result.Should().NotBeNull();
        result!.SectorEtf.Should().Be("XLK");
        result.RelativeReturn.Should().BeGreaterThan(0m);
        result.Score.Should().BeGreaterThan(0.5m);
    }

    [Fact]
    public async Task CalculateAsync_StockUnderperformsEtf_ReturnsNegativeRelativeReturnAndLowScore()
    {
        var stockCandles = FlatCandles(10, 100m);
        stockCandles[^1].Close = 90m; // -10% over the window
        _candleRepo.GetCandlesAsync("AAPL", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(stockCandles);
        _tiingo.GetDailyPricesAsync("XLK", Arg.Any<string>(), Arg.Any<string>()).Returns(FlatEtfPrices(10, 100m));

        var result = await CreateSut().CalculateAsync(_tiingo, "AAPL", CancellationToken.None);

        result.Should().NotBeNull();
        result!.RelativeReturn.Should().BeLessThan(0m);
        result.Score.Should().BeLessThan(0.5m);
    }

    [Fact]
    public async Task CalculateAsync_UnmappedSymbol_UsesSpyAsFallbackEtf()
    {
        _candleRepo.GetCandlesAsync("XYZQ", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(FlatCandles(10, 100m));
        _tiingo.GetDailyPricesAsync("SPY", Arg.Any<string>(), Arg.Any<string>()).Returns(FlatEtfPrices(10, 100m));

        var result = await CreateSut().CalculateAsync(_tiingo, "XYZQ", CancellationToken.None);

        result.Should().NotBeNull();
        result!.SectorEtf.Should().Be("SPY");
    }

    [Fact]
    public async Task CalculateAsync_SectorMapDrivesEtf_ForNonOverrideSymbols()
    {
        // XOM has no symbol override; with the GICS-driven map present it
        // benchmarks against XLE instead of the old SPY fallback.
        _candleRepo.GetCandlesAsync("XOM", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(FlatCandles(10, 100m));
        _tiingo.GetDailyPricesAsync("XLE", Arg.Any<string>(), Arg.Any<string>()).Returns(FlatEtfPrices(10, 100m));
        var sut = CreateSut();
        _universe.GetSectorEtfMapAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["XOM"] = "XLE" });

        var result = await sut.CalculateAsync(_tiingo, "XOM", CancellationToken.None);

        result.Should().NotBeNull();
        result!.SectorEtf.Should().Be("XLE");
    }

    [Fact]
    public async Task CalculateAsync_UniverseLookupThrows_DegradesToLegacyMap()
    {
        _candleRepo.GetCandlesAsync("AAPL", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(FlatCandles(10, 100m));
        _tiingo.GetDailyPricesAsync("XLK", Arg.Any<string>(), Arg.Any<string>()).Returns(FlatEtfPrices(10, 100m));
        var sut = CreateSut();
        _universe.GetSectorEtfMapAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, string>>>(_ => throw new InvalidOperationException("wiki down"));

        var result = await sut.CalculateAsync(_tiingo, "AAPL", CancellationToken.None);

        result.Should().NotBeNull();
        result!.SectorEtf.Should().Be("XLK"); // legacy override still wins
    }

    [Fact]
    public async Task CalculateAsync_CandleRepoThrows_ReturnsNullFallback()
    {
        _candleRepo.GetCandlesAsync("AAPL", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns<Task<IReadOnlyList<StockCandle>>>(_ => throw new InvalidOperationException("boom"));

        var result = await CreateSut().CalculateAsync(_tiingo, "AAPL", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAsync_SecondCallSameDay_UsesCachedEtfClosesNotSecondTiingoCall()
    {
        _candleRepo.GetCandlesAsync(Arg.Any<string>(), "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(FlatCandles(10, 100m));
        _tiingo.GetDailyPricesAsync("XLK", Arg.Any<string>(), Arg.Any<string>()).Returns(FlatEtfPrices(10, 100m));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(cache);

        await sut.CalculateAsync(_tiingo, "AAPL", CancellationToken.None);
        await sut.CalculateAsync(_tiingo, "MSFT", CancellationToken.None); // same sector ETF (XLK)

        await _tiingo.Received(1).GetDailyPricesAsync("XLK", Arg.Any<string>(), Arg.Any<string>());
    }
}
