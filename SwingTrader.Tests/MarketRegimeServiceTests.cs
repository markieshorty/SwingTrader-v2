using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class MarketRegimeServiceTests
{
    private readonly ITiingoClient _tiingo = Substitute.For<ITiingoClient>();
    private readonly IFinnhubClient _finnhub = Substitute.For<IFinnhubClient>();

    private static MarketRegimeService CreateSut(IMemoryCache? cache = null) =>
        new(cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<MarketRegimeService>.Instance);

    // 210 flat-then-shaped daily bars so both the 50-day and 200-day
    // averages are well-defined; the last bar's price is set explicitly by
    // the caller to control where it sits relative to those averages.
    private void SetupSpyHistory(decimal flatPrice, decimal? lastBarPrice = null)
    {
        var prices = new List<TiingoDailyPrice>();
        var start = DateTime.UtcNow.Date.AddDays(-210);
        for (var i = 0; i < 210; i++)
        {
            var close = (i == 209 && lastBarPrice.HasValue) ? lastBarPrice.Value : flatPrice;
            prices.Add(new TiingoDailyPrice(start.AddDays(i), close, close, close, close, 1_000_000, close, close, close, close, 1_000_000));
        }
        _tiingo.GetDailyPricesAsync("SPY", Arg.Any<string>(), Arg.Any<string>()).Returns(prices);
    }

    private void SetupVix(decimal vix) =>
        _finnhub.GetQuoteAsync("VIX").Returns(new FinnhubQuoteResponse(vix, null, null, null, null, null, null, 0));

    [Fact]
    public async Task GetCurrentRegimeAsync_InsufficientHistory_Throws()
    {
        _tiingo.GetDailyPricesAsync("SPY", Arg.Any<string>(), Arg.Any<string>())
            .Returns([new TiingoDailyPrice(DateTime.UtcNow, 100, 100, 100, 100, 1000, 100, 100, 100, 100, 1000)]);

        var sut = CreateSut();
        var act = () => sut.GetCurrentRegimeAsync(_tiingo, _finnhub);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_HighVix_ClassifiesAsCrisisRegardlessOfPrice()
    {
        SetupSpyHistory(100m);
        SetupVix(40m); // > 35 crisis threshold

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Crisis);
        result.Label.Should().Contain("Crisis");
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_PriceBelow200DayMa_ClassifiesAsBear()
    {
        // Flat history at 100, but the final bar collapses well under the
        // 200-day average - vix stays low so only the price/MA relationship
        // drives the classification.
        SetupSpyHistory(100m, lastBarPrice: 50m);
        SetupVix(15m);

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Bear);
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_ModeratelyElevatedVix_ClassifiesAsBearEvenIfPriceIsFine()
    {
        SetupSpyHistory(100m);
        SetupVix(30m); // > 25 but < 35

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Bear);
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_PriceAboveAveragesLowVix_ClassifiesAsBull()
    {
        SetupSpyHistory(100m);
        SetupVix(15m);

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Bull);
        result.Label.Should().Contain("Bull");
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_MissingVixQuote_FallsBackToNeutralDefault()
    {
        // CurrentPrice null on the VIX quote - service defaults to 20m rather
        // than blowing up on a missing/degraded data point.
        SetupSpyHistory(100m);
        _finnhub.GetQuoteAsync("VIX").Returns(new FinnhubQuoteResponse(null, null, null, null, null, null, null, 0));

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Vix.Should().Be(20m);
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_SecondCall_UsesCacheNotSecondTiingoCall()
    {
        SetupSpyHistory(100m);
        SetupVix(15m);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(cache);

        await sut.GetCurrentRegimeAsync(_tiingo, _finnhub);
        await sut.GetCurrentRegimeAsync(_tiingo, _finnhub);

        await _tiingo.Received(1).GetDailyPricesAsync("SPY", Arg.Any<string>(), Arg.Any<string>());
    }
}
