using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using Xunit;

namespace SwingTrader.Tests;

// Factor sleeve backtest engine (docs/sleeves-plan P2a). Synthetic bars:
// deterministic uptrends/downtrends so selection and equity maths are
// directly checkable.
public class FactorBacktesterTests
{
    private static DailyBar[] Series(int days, decimal start, decimal dailyRetPct, decimal volume = 1_000_000m)
    {
        var bars = new DailyBar[days];
        var date = new DateTime(2020, 1, 6); // a Monday
        var price = start;
        for (var i = 0; i < days; i++)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            bars[i] = new DailyBar(date, price, price, price, price, volume);
            price *= 1m + dailyRetPct / 100m;
            date = date.AddDays(1);
        }
        return bars;
    }

    [Fact]
    public void MomentumAt_IsTwelveMinusOne()
    {
        var up = Series(300, 100m, 0.1m);
        var m = FactorBacktester.MomentumAt(up, 260);
        // return from index 8 (260-252) to index 239 (260-21): 231 days of +0.1%
        var expected = up[239].Close / up[8].Close - 1m;
        m.Should().Be(expected);
        FactorBacktester.MomentumAt(up, 100).Should().BeNull(); // inside warmup
    }

    [Fact]
    public void PassesScreen_RejectsIlliquidAndInconsistent()
    {
        var liquid = Series(300, 100m, 0.1m);            // ~$100 x 1M shares/day
        var illiquid = Series(300, 100m, 0.1m, volume: 100m);
        var falling = Series(300, 100m, -0.1m);

        FactorBacktester.PassesScreen(liquid, 280).Should().BeTrue();
        FactorBacktester.PassesScreen(illiquid, 280).Should().BeFalse();
        FactorBacktester.PassesScreen(falling, 280).Should().BeFalse(); // 0 positive months
    }

    [Fact]
    public void Run_PicksTheUptrends_AndEquityFollowsThem()
    {
        var bars = new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = Series(600, 300m, 0.02m),
            ["WINA"] = Series(600, 50m, 0.15m),
            ["WINB"] = Series(600, 80m, 0.12m),
            ["LOSER"] = Series(600, 90m, -0.10m),
            ["VIX"] = Series(600, 20m, 0m),
        };

        var result = FactorBacktester.Run(bars);

        result.Mode.Should().Be("factor");
        result.Rebalances.Should().BeGreaterThan(10);
        // Only the two uptrends qualify; the loser never has positive momentum.
        result.FinalHoldings.Should().BeEquivalentTo(["WINA", "WINB"]);
        // Holding steady +0.12-0.15%/day compounding must beat SPY's +0.02%.
        result.TotalReturnPct.Should().BeGreaterThan(result.SpyReturnPct);
        result.HeldUp.Should().BeTrue();
    }

    [Fact]
    public void Run_DelistedHolding_SellsAtLastCloseWithoutCrashing()
    {
        var dying = Series(400, 60m, 0.2m); // strong momentum, then vanishes
        var bars = new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = Series(600, 300m, 0.02m),
            ["DEAD"] = dying,
            ["WINA"] = Series(600, 50m, 0.1m),
        };

        var result = FactorBacktester.Run(bars);

        result.FinalHoldings.Should().NotContain("DEAD");
        result.TotalReturnPct.Should().BeGreaterThan(0);
    }
}
