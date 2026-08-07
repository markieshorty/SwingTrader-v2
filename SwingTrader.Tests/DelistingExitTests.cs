using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Delisting semantics (docs/survivorship-plan P2): a position whose symbol's
// bars end mid-simulation must force-exit at the last close (haircut when the
// end reason is unknown; untouched for acquisitions) - never freeze forever.
public class DelistingExitTests
{
    // Build a bar world: SPY runs the whole calendar; DEADX is screen-passing
    // liquid and volatile enough to enter, then its bars simply stop.
    private static Dictionary<string, DailyBar[]> World(int deadLastDay, int totalDays)
    {
        var start = new DateTime(2020, 1, 6); // a Monday
        DailyBar Bar(int day, decimal close, decimal open) =>
            new(start.AddDays(day + (day / 5) * 2), open, close * 1.03m, close * 0.97m, close, 2_000_000);

        // SPY: flat drift, full calendar.
        var spy = Enumerable.Range(0, totalDays).Select(i => Bar(i, 400m + i * 0.1m, 400m + i * 0.1m)).ToArray();

        // DEADX: liquid mid-priced stock that takes a sharp oversold dip so
        // the screener/scorer picks it up, then keeps trading until its
        // delisting day.
        var dead = Enumerable.Range(0, deadLastDay + 1).Select(i =>
        {
            // steady $60, then an 8% drop over the last 10 bars of its life
            // 2.5%/day slide for its last 10 bars - inside the screener's 1-15%
            // daily-move band, deep enough to look oversold to the scorer.
            var close = i < deadLastDay - 10 ? 60m : 60m * (1 - 0.025m * (i - (deadLastDay - 10)));
            return Bar(i, close, close * 1.005m);
        }).ToArray();

        return new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = spy,
            ["DEADX"] = dead,
        };
    }

    private static HistoricConfig Config() => new(
        Weights: new StrategyWeights
        {
            RsiWeight = 0.17m, MacdWeight = 0.09m, VolumeWeight = 0.21m,
            SetupQualityWeight = 0.12m, RelativeStrengthWeight = 0.2m, PriceLevelWeight = 0.21m,
        },
        BuyThreshold: 0.1m,           // enter on anything scoreable
        ExcludeBreakout: false,
        SimulateProbation: false,     // keep the exit path deterministic
        MaxHoldDays: 200,             // no time exit before the delisting
        StopLossPct: 0.90m,           // stops/targets far out of reach
        TargetPct: 5.0m,
        MinDollarVolume: 1m,
        // These fixtures pre-date the union screen and are built for the
        // legacy one: DEADX is a steady 2.5%/day slide, which ranked top of a
        // "biggest movers" list but qualifies for NO setup screen - a pure
        // decliner fails OversoldRecovery's price > close[-4] recovery leg.
        // The subject here is delisting semantics, not candidate selection,
        // so pin the legacy screen and let ScreenerUnionBacktestTests cover
        // the union path.
        UnionScreen: false);

    [Fact]
    public async Task PositionInDelistedSymbol_ForceExitsWithHaircut()
    {
        var world = World(deadLastDay: 140, totalDays: 200);
        var result = await HistoricBacktester.RunAsync(world, Config());

        var delisted = result.TradeLog.Where(t => t.ExitReason == "Delisted").ToList();
        delisted.Should().NotBeEmpty("a position held past the symbol's last bar must exit, not freeze");
        // Unknown end reason (no lifecycle map) takes the 25% haircut.
        var lastClose = world["DEADX"][^1].Close;
        delisted[0].ExitPrice.Should().Be(lastClose * 0.75m);
    }

    [Fact]
    public async Task AcquisitionTaggedDelisting_ExitsAtLastCloseUntouched()
    {
        var world = World(deadLastDay: 140, totalDays: 200);
        var reasons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["DEADX"] = "acquisition" };
        var result = await HistoricBacktester.RunAsync(world, Config(), delistingReasons: reasons);

        var delisted = result.TradeLog.Where(t => t.ExitReason == "Delisted").ToList();
        delisted.Should().NotBeEmpty();
        delisted[0].ExitPrice.Should().Be(world["DEADX"][^1].Close);
    }
}
