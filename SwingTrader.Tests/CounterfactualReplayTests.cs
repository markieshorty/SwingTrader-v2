using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Agents.Scorecard;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The scorecard's counterfactual walk decides whether the vetoes are judged to
// have saved or cost money - the exit priorities and cost handling must match
// the backtester's conventions exactly.
public class CounterfactualReplayTests
{
    private static HistoricalCandle Bar(int day, decimal open, decimal high, decimal low, decimal close) =>
        new() { Symbol = "TEST", Date = new DateOnly(2026, 1, 1).AddDays(day), Open = open, High = high, Low = low, Close = close, Volume = 1000 };

    private static readonly DateOnly Signal = new(2026, 1, 2);

    [Fact]
    public void Run_EntersAtFirstBarAfterSignalDate_AtTheOpen()
    {
        var bars = new List<HistoricalCandle>
        {
            Bar(1, 100, 101, 99, 100),   // signal date bar
            Bar(2, 102, 103, 101, 102),  // entry bar
            Bar(3, 102, 115, 101, 112),  // target hit (+8% = 110.16)
        };

        var outcome = CounterfactualReplay.Run(bars, Signal, 0.05m, 0.08m, 10, 0.05m, 0.03m);

        outcome.Should().NotBeNull();
        outcome!.EntryDate.Should().Be(new DateOnly(2026, 1, 3));
        outcome.ExitReason.Should().Be("Target");
        outcome.ReturnPct.Should().BePositive();
    }

    [Fact]
    public void Run_StopBeatsTargetOnTheSameBar()
    {
        // A wide bar touching both levels exits at the stop - same conservative
        // priority as HistoricBacktester.CheckExit.
        var bars = new List<HistoricalCandle>
        {
            Bar(1, 100, 101, 99, 100),
            Bar(2, 100, 100, 100, 100),  // entry at 100
            Bar(3, 100, 120, 90, 100),   // touches stop (95) AND target (108)
        };

        var outcome = CounterfactualReplay.Run(bars, Signal, 0.05m, 0.08m, 10, 0.05m, 0.03m);

        outcome!.ExitReason.Should().Be("StopLoss");
        outcome.ReturnPct.Should().BeNegative();
    }

    [Fact]
    public void Run_GapDownBelowStop_ExitsAtTheOpenNotTheStop()
    {
        var bars = new List<HistoricalCandle>
        {
            Bar(1, 100, 101, 99, 100),
            Bar(2, 100, 100, 100, 100),  // entry at 100, stop 95
            Bar(3, 90, 92, 88, 91),      // gaps to 90 - fill is the open
        };

        var outcome = CounterfactualReplay.Run(bars, Signal, 0.05m, 0.08m, 10, 0.05m, 0.03m);

        outcome!.ExitReason.Should().Be("StopLoss");
        // Exit ~90 vs stop 95: the gap made it worse than the stop implies.
        outcome.ReturnPct.Should().BeLessThan(-9m);
    }

    [Fact]
    public void Run_TrailingArmsAtCloseAndExitsOnPullback()
    {
        var bars = new List<HistoricalCandle>
        {
            Bar(1, 100, 101, 99, 100),
            Bar(2, 100, 100, 100, 100),   // entry 100 (stop 94, target 120)
            Bar(3, 100, 106, 100, 106),   // +6% close arms the trail (act 5%): trail = 106*0.97 = 102.82
            Bar(4, 105, 105, 101, 103),   // low 101 <= 102.82 -> trailing exit
        };

        var outcome = CounterfactualReplay.Run(bars, Signal, 0.06m, 0.20m, 10, 0.05m, 0.03m);

        outcome!.ExitReason.Should().Be("Trailing");
        outcome.ReturnPct.Should().BePositive(); // banked most of the +6%
    }

    [Fact]
    public void Run_OutOfBars_MarksStillOpenAtLastClose()
    {
        var bars = new List<HistoricalCandle>
        {
            Bar(1, 100, 101, 99, 100),
            Bar(2, 100, 102, 100, 101),   // entry 100
            Bar(3, 101, 103, 100, 102),   // no exit level touched
        };

        var outcome = CounterfactualReplay.Run(bars, Signal, 0.10m, 0.20m, 10, 0.10m, 0.05m);

        outcome!.StillOpen.Should().BeTrue();
        outcome.ExitReason.Should().Be("StillOpen");
        outcome.ExitDate.Should().BeNull();
    }

    [Fact]
    public void Run_NoBarAfterSignal_ReturnsNull()
    {
        var bars = new List<HistoricalCandle> { Bar(0, 100, 101, 99, 100), Bar(1, 100, 101, 99, 100) };

        CounterfactualReplay.Run(bars, Signal, 0.05m, 0.08m, 10, 0.05m, 0.03m).Should().BeNull();
    }

    [Fact]
    public void Run_CostsMatchTheBacktesterConvention()
    {
        // Flat round trip at the same price: return = -2 x 0.25% (approx).
        var bars = new List<HistoricalCandle>
        {
            Bar(1, 100, 101, 99, 100),
            Bar(2, 100, 100, 100, 100),
            Bar(3, 100, 100, 100, 100),
        };

        var outcome = CounterfactualReplay.Run(bars, Signal, 0.10m, 0.20m, 10, 0.10m, 0.05m);

        outcome!.StillOpen.Should().BeTrue();
        outcome.ReturnPct.Should().BeApproximately(-0.50m, 0.01m);
    }
}

public class ForwardScorecardMathTests
{
    [Fact]
    public void Pearson_PerfectPositiveCorrelation_IsOne()
    {
        var pairs = new List<(decimal, decimal)> { (1, 2), (2, 4), (3, 6), (4, 8) };
        ForwardScorecardService.Pearson(pairs).Should().Be(1.000m);
    }

    [Fact]
    public void Pearson_PerfectNegativeCorrelation_IsMinusOne()
    {
        var pairs = new List<(decimal, decimal)> { (1, -2), (2, -4), (3, -6) };
        ForwardScorecardService.Pearson(pairs).Should().Be(-1.000m);
    }

    [Fact]
    public void Pearson_TooFewPairsOrConstantSeries_IsNull()
    {
        ForwardScorecardService.Pearson([(1, 2), (2, 3)]).Should().BeNull();
        ForwardScorecardService.Pearson([(1, 5), (2, 5), (3, 5)]).Should().BeNull();
    }
}

public class VixCsvParseTests
{
    [Fact]
    public void ParseVixCsv_ParsesCboeFormatAndOrdersByDate()
    {
        var csv = "DATE,OPEN,HIGH,LOW,CLOSE\n" +
                  "01/03/2020,13.46,16.20,13.13,14.02\n" +
                  "01/02/2020,13.46,13.72,12.42,12.47\n" +
                  "garbage line\n" +
                  "03/16/2020,57.83,83.56,56.88,82.69\n";

        var candles = CandleSyncService.ParseVixCsv(csv);

        candles.Should().HaveCount(3);
        candles[0].Date.Should().Be(new DateOnly(2020, 1, 2));
        candles[^1].Close.Should().Be(82.69m); // the COVID spike row parses (Crisis territory)
        candles.Should().OnlyContain(c => c.Symbol == "VIX" && c.Volume == 0);
    }
}
