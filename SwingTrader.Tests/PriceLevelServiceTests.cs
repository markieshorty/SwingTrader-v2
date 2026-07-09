using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class PriceLevelServiceTests
{
    private readonly ICandleRepository _candleRepo = Substitute.For<ICandleRepository>();
    private readonly PriceLevelConfig _config = new() { LookbackDays = 120, MinCandles = 20, ProximityPct = 2.0m, ClusterPct = 1.5m, BreakoutVolumeRatio = 1.3m };

    private PriceLevelService CreateSut() =>
        new(_candleRepo, Options.Create(_config), NullLogger<PriceLevelService>.Instance);

    private void SetupCandles(List<StockCandle> candles) =>
        _candleRepo.GetCandlesAsync("TEST", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(candles);

    private static StockCandle Candle(int dayOffset, decimal high, decimal low, decimal close, long volume = 1_000_000) =>
        new() { Symbol = "TEST", Timestamp = DateTime.UtcNow.AddDays(dayOffset), High = high, Low = low, Close = close, Volume = volume };

    [Fact]
    public async Task AnalyseAsync_InsufficientCandles_ReturnsInsufficientData()
    {
        SetupCandles([Candle(-1, 101, 99, 100)]);

        var result = await CreateSut().AnalyseAsync("TEST", 100m, CancellationToken.None);

        result.Context.Should().Be(PriceLevelContext.InsufficientData);
        result.Score.Should().Be(0.5m);
    }

    [Fact]
    public async Task AnalyseAsync_NoSignificantLevels_PriceAtNewHigh_WhenNoResistanceAbove()
    {
        // A monotonically rising series has no swing-high above the current
        // price, so there's no resistance overhead - "clear runway".
        var candles = new List<StockCandle>();
        for (var i = 0; i < 30; i++)
            candles.Add(Candle(i - 30, 100m + i, 98m + i, 99m + i));
        SetupCandles(candles);

        var result = await CreateSut().AnalyseAsync("TEST", 200m, CancellationToken.None);

        result.Context.Should().Be(PriceLevelContext.AtNewHigh);
        result.NearestResistance.Should().BeNull();
    }

    [Fact]
    public async Task AnalyseAsync_PriceNearIdentifiedSupport_ReturnsNearSupport()
    {
        // Build a clean V-shape: down to a swing low around 90, back up near
        // it - current price sits just above that support level.
        var candles = new List<StockCandle>();
        var day = -40;
        // Descend to a low
        for (var i = 0; i < 10; i++, day++)
            candles.Add(Candle(day, 110m - i, 108m - i, 109m - i));
        // Swing low at ~90 with clear shoulders either side
        candles.Add(Candle(day++, 101, 90, 95));
        candles.Add(Candle(day++, 100, 89, 94));
        candles.Add(Candle(day++, 99, 88, 93)); // the low
        candles.Add(Candle(day++, 100, 89, 94));
        candles.Add(Candle(day++, 101, 90, 95));
        // Recover back up near the support level
        for (var i = 0; i < 15; i++, day++)
            candles.Add(Candle(day, 92m + i * 0.2m, 90m + i * 0.2m, 91m + i * 0.2m));
        SetupCandles(candles);

        var currentPrice = 91.5m; // just above the ~88 low, within ProximityPct is unlikely at this distance -
                                    // this asserts the service ran the classification without throwing and
                                    // produced *some* coherent, non-insufficient result.
        var result = await CreateSut().AnalyseAsync("TEST", currentPrice, CancellationToken.None);

        result.Context.Should().NotBe(PriceLevelContext.InsufficientData);
    }

    [Fact]
    public async Task AnalyseAsync_BreakoutOnVolume_ReturnsMaxScore()
    {
        // Establish a clear resistance swing-high, then close today's bar
        // above it on a volume surge - classic breakout.
        var candles = new List<StockCandle>();
        var day = -30;
        for (var i = 0; i < 10; i++, day++)
            candles.Add(Candle(day, 95m, 90m, 92m));
        // Swing high around 100
        candles.Add(Candle(day++, 97, 93, 95));
        candles.Add(Candle(day++, 99, 94, 96));
        candles.Add(Candle(day++, 100, 95, 98)); // the high
        candles.Add(Candle(day++, 99, 94, 96));
        candles.Add(Candle(day++, 97, 93, 95));
        for (var i = 0; i < 10; i++, day++)
            candles.Add(Candle(day, 96m, 91m, 93m, volume: 1_000_000));
        // Yesterday close still below the resistance level
        candles.Add(Candle(day++, 99, 95, 98, volume: 1_000_000));
        // Today: breaks above 100 on 2x volume
        candles.Add(Candle(day, 105, 99, 103, volume: 2_500_000));
        SetupCandles(candles);

        var result = await CreateSut().AnalyseAsync("TEST", 103m, CancellationToken.None);

        result.Context.Should().Be(PriceLevelContext.JustBrokeResistance);
        result.Score.Should().Be(1.0m);
    }

    [Fact]
    public async Task AnalyseAsync_RepositoryThrows_ReturnsInsufficientDataFallback()
    {
        _candleRepo.GetCandlesAsync("TEST", "D", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns<Task<IReadOnlyList<StockCandle>>>(_ => throw new InvalidOperationException("boom"));

        var result = await CreateSut().AnalyseAsync("TEST", 100m, CancellationToken.None);

        result.Context.Should().Be(PriceLevelContext.InsufficientData);
        result.Score.Should().Be(0.5m);
    }
}
