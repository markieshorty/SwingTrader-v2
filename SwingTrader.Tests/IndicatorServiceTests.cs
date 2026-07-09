using FluentAssertions;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

public class IndicatorServiceTests
{
    private readonly IndicatorService _sut = new();

    // Deterministic upward-drifting candle series with mild noise, enough
    // bars for every indicator (MACD needs slow(26)+signal(9)=35) to compute.
    private static List<StockCandle> BuildCandles(int count, decimal start = 100m, decimal drift = 0.3m)
    {
        var candles = new List<StockCandle>();
        var price = start;
        for (var i = 0; i < count; i++)
        {
            // Small oscillation so RSI/MACD aren't degenerate (pure straight line).
            var wiggle = (i % 3 == 0) ? -0.5m : 0.2m;
            price = Math.Max(1m, price + drift + wiggle);
            candles.Add(new StockCandle
            {
                Symbol = "TEST",
                Timestamp = DateTime.UtcNow.AddDays(i - count),
                Open = price - 0.5m,
                High = price + 1m,
                Low = price - 1m,
                Close = price,
                Volume = 1_000_000 + i * 1000,
            });
        }
        return candles;
    }

    [Fact]
    public async Task GetRsiAsync_InsufficientCandles_ReturnsNull()
    {
        var result = await _sut.GetRsiAsync(BuildCandles(10), period: 14);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRsiAsync_SufficientCandles_ReturnsValueWithinBounds()
    {
        var result = await _sut.GetRsiAsync(BuildCandles(30), period: 14);
        result.Should().NotBeNull();
        result!.Value.Should().BeInRange(0m, 100m);
    }

    [Fact]
    public async Task GetRsiAsync_ConsistentlyRisingPrices_ReturnsHighRsi()
    {
        // A candle series that only ever rises should read as strongly
        // overbought (high RSI), not a middling/neutral value.
        var candles = new List<StockCandle>();
        var price = 100m;
        for (var i = 0; i < 30; i++)
        {
            price += 1m;
            candles.Add(new StockCandle { Timestamp = DateTime.UtcNow.AddDays(i - 30), Open = price, High = price + 1, Low = price - 1, Close = price, Volume = 1000 });
        }

        var result = await _sut.GetRsiAsync(candles);
        result.Should().NotBeNull();
        result!.Value.Should().BeGreaterThan(70m);
    }

    [Fact]
    public async Task GetMacdAsync_InsufficientCandles_ReturnsAllNull()
    {
        var (macd, signal, hist) = await _sut.GetMacdAsync(BuildCandles(20));
        macd.Should().BeNull();
        signal.Should().BeNull();
        hist.Should().BeNull();
    }

    [Fact]
    public async Task GetMacdAsync_SufficientCandles_ReturnsValues()
    {
        var (macd, signal, hist) = await _sut.GetMacdAsync(BuildCandles(50));
        macd.Should().NotBeNull();
        signal.Should().NotBeNull();
        hist.Should().NotBeNull();
    }

    [Fact]
    public async Task GetBollingerBandsAsync_SufficientCandles_UpperAboveLower()
    {
        var (upper, mid, lower) = await _sut.GetBollingerBandsAsync(BuildCandles(30));
        upper.Should().NotBeNull();
        lower.Should().NotBeNull();
        mid.Should().NotBeNull();
        upper!.Value.Should().BeGreaterThan(lower!.Value);
        mid!.Value.Should().BeInRange(lower.Value, upper.Value);
    }

    [Fact]
    public async Task GetBollingerBandsAsync_InsufficientCandles_ReturnsNull()
    {
        var (upper, mid, lower) = await _sut.GetBollingerBandsAsync(BuildCandles(5), period: 20);
        upper.Should().BeNull();
        mid.Should().BeNull();
        lower.Should().BeNull();
    }

    [Fact]
    public async Task GetEmaAsync_InsufficientCandles_ReturnsNull()
    {
        var result = await _sut.GetEmaAsync(BuildCandles(5), period: 9);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetEmaAsync_SufficientCandles_ReturnsPositiveValue()
    {
        var result = await _sut.GetEmaAsync(BuildCandles(30), period: 9);
        result.Should().NotBeNull();
        result!.Value.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task GetVolumeRatioAsync_TodayDoubleAverage_ReturnsAboutTwo()
    {
        var candles = new List<StockCandle>();
        for (var i = 0; i < 20; i++)
            candles.Add(new StockCandle { Timestamp = DateTime.UtcNow.AddDays(i - 21), Close = 100m, Volume = 1_000_000 });
        candles.Add(new StockCandle { Timestamp = DateTime.UtcNow, Close = 100m, Volume = 2_000_000 });

        var ratio = await _sut.GetVolumeRatioAsync(candles, avgPeriod: 20);

        ratio.Should().NotBeNull();
        ratio!.Value.Should().BeApproximately(2.0m, 0.05m);
    }

    [Fact]
    public async Task GetVolumeRatioAsync_SingleCandle_ReturnsNull()
    {
        var result = await _sut.GetVolumeRatioAsync([new StockCandle { Timestamp = DateTime.UtcNow, Volume = 1000 }]);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAllAsync_ReturnsPopulatedResultForSufficientData()
    {
        var result = await _sut.CalculateAllAsync(BuildCandles(50));

        result.Rsi14.Should().NotBeNull();
        result.Macd.Should().NotBeNull();
        result.BollingerUpper.Should().NotBeNull();
        result.Ema9.Should().NotBeNull();
        result.Ema21.Should().NotBeNull();
        result.VolumeRatio.Should().NotBeNull();
    }

    [Fact]
    public void Calculate_SyncWrapper_MatchesAsyncResult()
    {
        var candleData = BuildCandles(50).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        var result = _sut.Calculate(candleData);

        result.Rsi14.Should().NotBeNull();
        result.Macd.Should().NotBeNull();
    }

    // ── CalculateSharpeRatio ──────────────────────────────────────────────

    [Fact]
    public void CalculateSharpeRatio_FewerThanTenReturns_ReturnsNull()
    {
        var returns = Enumerable.Repeat(0.01m, 9);
        _sut.CalculateSharpeRatio(returns).Should().BeNull();
    }

    [Fact]
    public void CalculateSharpeRatio_ZeroVariance_ReturnsNull()
    {
        // All identical returns -> zero standard deviation -> undefined ratio.
        var returns = Enumerable.Repeat(0.01m, 12);
        _sut.CalculateSharpeRatio(returns).Should().BeNull();
    }

    [Fact]
    public void CalculateSharpeRatio_PositiveConsistentReturns_ReturnsPositiveRatio()
    {
        // Alternating slightly above/below a positive mean - enough variance
        // for a defined ratio, and the mean excess return is clearly positive.
        var returns = new List<decimal>();
        for (var i = 0; i < 20; i++)
            returns.Add(i % 2 == 0 ? 0.05m : 0.03m);

        var result = _sut.CalculateSharpeRatio(returns);

        result.Should().NotBeNull();
        result!.Value.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void CalculateSharpeRatio_NegativeReturns_ReturnsNegativeRatio()
    {
        var returns = new List<decimal>();
        for (var i = 0; i < 20; i++)
            returns.Add(i % 2 == 0 ? -0.05m : -0.03m);

        var result = _sut.CalculateSharpeRatio(returns);

        result.Should().NotBeNull();
        result!.Value.Should().BeLessThan(0m);
    }

    // ── CalculateMaxDrawdown ──────────────────────────────────────────────

    [Fact]
    public void CalculateMaxDrawdown_SingleValue_ReturnsZero()
    {
        _sut.CalculateMaxDrawdown([100m]).Should().Be(0m);
    }

    [Fact]
    public void CalculateMaxDrawdown_MonotonicallyRising_ReturnsZero()
    {
        _sut.CalculateMaxDrawdown([100m, 110m, 120m, 130m]).Should().Be(0m);
    }

    [Fact]
    public void CalculateMaxDrawdown_PeakThenDrop_ReturnsCorrectPct()
    {
        // Peaks at 200, drops to 150 -> 25% drawdown.
        var result = _sut.CalculateMaxDrawdown([100m, 200m, 150m]);
        result.Should().Be(0.25m);
    }

    [Fact]
    public void CalculateMaxDrawdown_MultipleDrawdowns_ReturnsTheLargest()
    {
        // First drawdown: 100 -> 200 -> 180 = 10%. Second: 180 -> 300 -> 210 = 30%.
        var result = _sut.CalculateMaxDrawdown([100m, 200m, 180m, 300m, 210m]);
        result.Should().Be(0.30m);
    }

    [Fact]
    public void CalculateMaxDrawdown_RecoversAfterDrawdown_StillReportsThePeakDrawdown()
    {
        var result = _sut.CalculateMaxDrawdown([100m, 200m, 100m, 250m]);
        result.Should().Be(0.5m);
    }
}
