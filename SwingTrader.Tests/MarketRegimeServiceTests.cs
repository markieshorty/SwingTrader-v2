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
    public async Task GetCurrentRegimeAsync_ModeratelyElevatedVix_IsNeutralNotBear()
    {
        // A VIX spike during a structurally healthy market is a correction,
        // not a bear - the old classifier called this Bear, which would have
        // made the bear autopause flap on routine volatility.
        SetupSpyHistory(100m);
        SetupVix(30m); // > 25 but < 35, price fine

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Neutral);
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
    public async Task GetCurrentRegimeAsync_MissingVixQuote_ClassifiesOnPriceStructureAlone()
    {
        // No fabricated VIX: the old fallback invented a 20m reading, which
        // silently steered classification. Unknown VIX now means the VIX
        // conditions simply don't apply - healthy price structure = Bull.
        SetupSpyHistory(100m);
        _finnhub.GetQuoteAsync("VIX").Returns(new FinnhubQuoteResponse(null, null, null, null, null, null, null, 0));

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Bull);
        result.Label.Should().Contain("VIX n/a");
    }

    [Fact]
    public async Task GetCurrentRegimeAsync_ShallowDipBelowRising200Dma_IsNeutralNotBear()
    {
        // Rising history (so the 200dma is rising and the 50dma sits above the
        // 200dma), final bar dips ~1% below the 200dma: a pullback, not a
        // structural bear - must NOT trigger the bear autopause.
        var prices = new List<TiingoDailyPrice>();
        var start = DateTime.UtcNow.Date.AddDays(-320);
        for (var i = 0; i < 219; i++)
        {
            var price = 90m + i * 0.1m; // steady uptrend
            prices.Add(new TiingoDailyPrice(start.AddDays(i), price, price, price, price, 1, price, price, price, price, 1));
        }
        // 200dma of last 200 bars ~ around 101.8; dip just below it.
        var dip = 101.0m;
        prices.Add(new TiingoDailyPrice(start.AddDays(220), dip, dip, dip, dip, 1, dip, dip, dip, dip, 1));
        _tiingo.GetDailyPricesAsync("SPY", Arg.Any<string>(), Arg.Any<string>()).Returns(prices);
        SetupVix(18m);

        var result = await CreateSut().GetCurrentRegimeAsync(_tiingo, _finnhub);

        result.Regime.Should().Be(MarketRegime.Neutral);
    }

    // ── ClassifyFromCloses: historical, price-structure-only (backtester) ──────

    [Fact]
    public void ClassifyFromCloses_InsufficientHistory_ReturnsNull()
    {
        var closes = Enumerable.Repeat(100m, 150).ToList();
        MarketRegimeService.ClassifyFromCloses(closes).Should().BeNull();
    }

    [Fact]
    public void ClassifyFromCloses_PriceAboveAverages_IsBull()
    {
        // Steady uptrend: last close sits above both the 50- and 200-day MAs.
        var closes = Enumerable.Range(0, 220).Select(i => 50m + i * 0.5m).ToList();
        MarketRegimeService.ClassifyFromCloses(closes).Should().Be(MarketRegime.Bull);
    }

    [Fact]
    public void ClassifyFromCloses_DeepBreachBelow200Ma_IsBear()
    {
        // Flat at 100, final close collapses well below the 200-day average.
        var closes = Enumerable.Repeat(100m, 219).Append(50m).ToList();
        MarketRegimeService.ClassifyFromCloses(closes).Should().Be(MarketRegime.Bear);
    }

    [Fact]
    public void ClassifyFromCloses_NoVix_NeverReturnsCrisis()
    {
        // Without a VIX reading, Crisis (VIX-driven) can't be detected - the
        // backtester's Mixed mode relies on this being price-structure only.
        var flat = Enumerable.Repeat(100m, 220).ToList();
        var crash = Enumerable.Repeat(100m, 219).Append(40m).ToList();
        MarketRegimeService.ClassifyFromCloses(flat).Should().NotBe(MarketRegime.Crisis);
        MarketRegimeService.ClassifyFromCloses(crash).Should().NotBe(MarketRegime.Crisis);
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
