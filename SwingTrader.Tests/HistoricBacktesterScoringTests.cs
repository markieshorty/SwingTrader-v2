using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

// Wiring of the shared RS/price-level calculators into the historic engine's
// scoring: date-aligned ETF windows, no lookahead past the scoring day,
// graceful null when ETF bars are missing, and conviction actually moving
// when RS/price-level weights move (they're live dials now, not dead ones).
public class HistoricBacktesterScoringTests
{
    private static readonly DateTime Start = new(2024, 1, 1);

    private static DailyBar[] Bars(int count, Func<int, decimal> close, Func<int, decimal>? volume = null) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var c = close(i);
            return new DailyBar(Start.AddDays(i), c, c + 0.5m, c - 0.5m, c, volume?.Invoke(i) ?? 1_000_000m);
        }).ToArray();

    private static Dictionary<string, Dictionary<DateTime, int>> Index(IReadOnlyDictionary<string, DailyBar[]> bars) =>
        bars.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void RelativeStrength_MatchesSharedCalculator_OnDateAlignedWindows()
    {
        // AAPL maps to XLK. Stock trends up ~0.4%/day, ETF flat.
        var stock = Bars(100, i => 100m + i * 0.4m);
        var etf = Bars(100, _ => 100m);
        var bars = new Dictionary<string, DailyBar[]> { ["AAPL"] = stock, ["XLK"] = etf };
        var today = stock[99].Date;
        var history = stock[(99 - HistoricBacktester.WarmupBars + 1)..100];

        var score = HistoricBacktester.ComputeRelativeStrengthScore(bars, Index(bars), "AAPL", history, today);

        var expected = RelativeStrengthCalculator.Compute(
            stock[95..100].Select(b => b.Close).ToList(),
            etf[95..100].Select(b => b.Close).ToList())!.Score;
        score.Should().Be(expected);
    }

    [Fact]
    public void RelativeStrength_IgnoresEtfBarsAfterTheScoringDay()
    {
        // ETF flat through day 89, then explodes +50%/day. Scoring at day 89
        // must see the flat window only - the future bars change nothing.
        var stock = Bars(95, i => 100m + i * 0.4m);
        var etfWithFuture = Bars(95, i => i <= 89 ? 100m : 100m * (1m + (i - 89) * 0.5m));
        var bars = new Dictionary<string, DailyBar[]> { ["AAPL"] = stock, ["XLK"] = etfWithFuture };
        var today = stock[89].Date;
        var history = stock[(89 - HistoricBacktester.WarmupBars + 1)..90];

        var score = HistoricBacktester.ComputeRelativeStrengthScore(bars, Index(bars), "AAPL", history, today);

        // Same as if the future never existed.
        var flatOnly = new Dictionary<string, DailyBar[]> { ["AAPL"] = stock[..90], ["XLK"] = Bars(90, _ => 100m) };
        var expected = HistoricBacktester.ComputeRelativeStrengthScore(
            flatOnly, Index(flatOnly), "AAPL", history, today);
        score.Should().Be(expected).And.NotBeNull();
    }

    [Fact]
    public void RelativeStrength_MissingEtfBars_ReturnsNull()
    {
        var stock = Bars(100, i => 100m + i * 0.4m);
        var bars = new Dictionary<string, DailyBar[]> { ["AAPL"] = stock }; // no XLK
        var history = stock[(99 - HistoricBacktester.WarmupBars + 1)..100];

        HistoricBacktester.ComputeRelativeStrengthScore(bars, Index(bars), "AAPL", history, stock[99].Date)
            .Should().BeNull();
    }

    [Fact]
    public async Task Conviction_ShiftsWhenRelativeStrengthAndPriceLevelWeightsShift()
    {
        // A stock strongly outperforming its flat sector ETF: RS score is
        // high, so loading weight onto RelativeStrength must raise conviction
        // relative to loading the same weight onto PriceLevel (the flat-ish
        // series scores mid/low on price level). If RS/PL were still dead
        // (fixed 0.5), both configs would produce the same conviction.
        var stock = Bars(100, i => 100m + i * 0.6m, i => 1_000_000m + i * 5_000m);
        var etf = Bars(100, _ => 100m);
        var bars = new Dictionary<string, DailyBar[]> { ["AAPL"] = stock, ["XLK"] = etf };
        var index = Index(bars);
        var indicators = new IndicatorService();
        var today = stock[99].Date;

        HistoricConfig Config(decimal rs, decimal pl) => new(new StrategyWeights
        {
            RsiWeight = 0.15m, MacdWeight = 0.15m, VolumeWeight = 0.15m,
            SetupQualityWeight = 0.15m, RelativeStrengthWeight = rs, PriceLevelWeight = pl,
        });

        var rsHeavy = await HistoricBacktester.ScoreAsync(indicators, Config(0.45m, 0.05m), bars, index, "AAPL", today);
        var plHeavy = await HistoricBacktester.ScoreAsync(indicators, Config(0.05m, 0.45m), bars, index, "AAPL", today);

        rsHeavy.Should().NotBeNull();
        plHeavy.Should().NotBeNull();
        rsHeavy!.Value.Conviction.Should().NotBe(plHeavy!.Value.Conviction);
        rsHeavy.Value.Conviction.Should().BeGreaterThan(plHeavy.Value.Conviction);
    }
}
