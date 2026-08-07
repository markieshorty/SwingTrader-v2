using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// docs/screener-union-plan. The backtester carried its own hardcoded copy of
// the legacy single-factor screen, so after the live screener moved to
// per-setup union pools every backtest was measuring a candidate supply
// production no longer used - which would have made per-setup conclusions
// (notably "Breakout is the drag") untransferable without anyone noticing.
public class ScreenerUnionBacktestTests
{
    // SPY plus one symbol that trends QUIETLY: ~0.35% a day, never enough to
    // clear the legacy screen's 1% minimum-move floor, but a textbook
    // TrendFollowing candidate (EMA9 > EMA21, RSI > 50, above the mid band).
    private static Dictionary<string, DailyBar[]> World(int totalDays = 200)
    {
        var start = new DateTime(2020, 1, 6); // a Monday
        DailyBar Bar(DateTime d, decimal close) =>
            new(d, close * 0.999m, close * 1.004m, close * 0.996m, close, 2_000_000);

        DateTime Day(int i) => start.AddDays(i + (i / 5) * 2);

        var spy = Enumerable.Range(0, totalDays)
            .Select(i => Bar(Day(i), 400m + i * 0.1m)).ToArray();

        var quietCloses = new decimal[totalDays];
        var px = 60m;
        for (var i = 0; i < totalDays; i++)
        {
            // +0.5%, +0.5%, -0.667%: drifts up ~0.33% every three days, with
            // no single day near the legacy screen's 1% floor. The down leg is
            // deliberately BIGGER than the up leg so average-gain / average-loss
            // lands RSI around 60 - a monotonic riser pins RSI at 100, and even
            // a 2:1 up/down rhythm reaches ~80, which the entry gate rejects
            // outright (`s.Rsi <= 75m`). A real quiet trend is not one-sided.
            px *= i % 3 == 2 ? 0.993333m : 1.005m;
            quietCloses[i] = px;
        }
        var quiet = Enumerable.Range(0, totalDays).Select(i => Bar(Day(i), quietCloses[i])).ToArray();

        return new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = spy,
            ["QUIET"] = quiet,
        };
    }

    private static HistoricConfig Config(bool union) => new(
        Weights: new StrategyWeights
        {
            RsiWeight = 0.17m, MacdWeight = 0.09m, VolumeWeight = 0.21m,
            SetupQualityWeight = 0.12m, RelativeStrengthWeight = 0.2m, PriceLevelWeight = 0.21m,
        },
        BuyThreshold: 0.1m,
        ExcludeBreakout: false,
        SimulateProbation: false,
        MinDollarVolume: 1m,
        UnionScreen: union);

    [Fact]
    public async Task QuietTrender_IsInvisibleToTheLegacyScreen()
    {
        // 0.35% a day never clears MinAbsChangePercent = 1%, so the old screen
        // could not surface this name however good the setup was.
        var result = await HistoricBacktester.RunAsync(World(), Config(union: false));

        result.TradeLog.Should().BeEmpty(
            "a sub-1% daily mover cannot pass a screen that ranks on absolute move");
    }

    [Fact]
    public void QuietTrender_QualifiesForTheTrendFollowingScreen()
    {
        // Pin the screen itself, independently of the whole backtest, so a
        // failure downstream cannot be mistaken for a screening failure.
        var world = World();
        var bars = world["QUIET"];
        var history = bars[^HistoricBacktester.WarmupBars..];
        var candles = history
            .Select(b => new SwingTrader.Infrastructure.Services.CandleData(
                b.Date, b.Open, b.High, b.Low, b.Close, (long)b.Volume))
            .ToList();

        var ind = new SwingTrader.Infrastructure.Services.IndicatorService().Calculate(candles);
        var found = SwingTrader.Agents.Watchlist.SetupScreens
            .Evaluate("QUIET", ind, history[^1].Close, history[^5].Close).ToList();

        found.Should().Contain(c => c.Setup == SwingTrader.Core.Enums.SetupType.TrendFollowing,
            $"a steady sub-1% riser is a TrendFollowing candidate (rsi={ind.Rsi14}, ema9={ind.Ema9}, " +
            $"ema21={ind.Ema21}, mid={ind.BollingerMid}, price={history[^1].Close})");
    }

    [Fact]
    public async Task QuietTrender_IsSurfacedByTheUnionScreen()
    {
        // Same world, same everything else - only the screen changes.
        var result = await HistoricBacktester.RunAsync(World(), Config(union: true));

        result.TradeLog.Should().NotBeEmpty(
            "TrendFollowing needs no volume and no move, so its candidates must reach the book");
    }

    [Fact]
    public void Fingerprint_SeparatesTheTwoScreeningRegimes()
    {
        // Evidence from a union run and a legacy run describes different
        // candidate populations and must never be pooled - the same mistake
        // that cost the factor sleeve and the first 41 filing events.
        ConfigFingerprint.Compute(Config(union: true))
            .Should().NotBe(ConfigFingerprint.Compute(Config(union: false)));
    }
}
