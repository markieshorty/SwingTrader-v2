using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The trading-rule overrides on HistoricConfig (Lab "Trading rules" panel):
// setup exclusions beyond Breakout, hold-cap override, and the trailing shape
// that used to be hardcoded 5%/3% constants. Driven through RunAsync over a
// deterministic synthetic market (no RNG anywhere in the engine).
public class HistoricBacktesterRulesTests
{
    private static readonly DateTime Start = new(2023, 1, 2);

    // An oscillating stock (big enough daily swings to pass the screener's
    // 1% move floor, RSI cycling through moderate values) over a flat-ish SPY.
    private static Dictionary<string, DailyBar[]> Market(int days = 250)
    {
        DailyBar Bar(int i, decimal close) =>
            new(Start.AddDays(i), close, close + 1.5m, close - 1.5m, close, 2_000_000m);

        var spy = Enumerable.Range(0, days).Select(i => Bar(i, 100m + i * 0.02m)).ToArray();
        var stock = Enumerable.Range(0, days)
            .Select(i => Bar(i, 100m + 10m * (decimal)Math.Sin(i / 3.0)))
            .ToArray();
        return new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = spy,
            ["OSC"] = stock,
        };
    }

    // Threshold 3 = take everything scoreable; the tests below vary RULES.
    private static HistoricConfig Config() => new(new StrategyWeights(), BuyThreshold: 3.0m);

    [Fact]
    public async Task Baseline_ProducesTrades_SoRuleTestsHaveSignal()
    {
        var result = await HistoricBacktester.RunAsync(Market(), Config());

        result.Trades.Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task ExcludedSetups_BlocksEveryEntryOfThatSetup()
    {
        var baseline = await HistoricBacktester.RunAsync(Market(), Config());
        // Pick whichever setup the synthetic market actually produces most.
        var dominant = baseline.TradeLog.GroupBy(t => t.Setup).OrderByDescending(g => g.Count()).First().Key;

        var excluded = await HistoricBacktester.RunAsync(
            Market(), Config() with { ExcludedSetups = [dominant] });

        baseline.TradeLog.Should().Contain(t => t.Setup == dominant);
        excluded.TradeLog.Should().NotContain(t => t.Setup == dominant);
    }

    [Fact]
    public async Task ExcludedSetupsNull_FallsBackToExcludeBreakoutToggle()
    {
        // With ExcludedSetups null and ExcludeBreakout false, Breakout entries
        // are allowed; the explicit empty set behaves identically - the
        // original toggle semantics are preserved for legacy requests.
        var viaToggle = await HistoricBacktester.RunAsync(
            Market(), Config() with { ExcludeBreakout = false });
        var viaEmptySet = await HistoricBacktester.RunAsync(
            Market(), Config() with { ExcludeBreakout = true, ExcludedSetups = Array.Empty<SetupType>() });

        viaEmptySet.Trades.Should().Be(viaToggle.Trades);
    }

    [Fact]
    public async Task MaxHoldDaysOverride_ShortensTimeExits()
    {
        var longHold = await HistoricBacktester.RunAsync(Market(), Config() with { MaxHoldDays = 10 });
        var shortHold = await HistoricBacktester.RunAsync(Market(), Config() with { MaxHoldDays = 2 });

        int MaxSpanDays(HistoricResult r) => r.TradeLog.Max(t => (t.ExitDate - t.EntryDate).Days);
        // Bars are consecutive calendar days in this fixture, so the held span
        // maps 1:1 to bar-index distance: a 2-bar cap must exit within 3 days.
        MaxSpanDays(shortHold).Should().BeLessThanOrEqualTo(3);
        MaxSpanDays(longHold).Should().BeGreaterThan(MaxSpanDays(shortHold));
    }

    [Fact]
    public async Task TrailingOverride_TightTrail_ProducesTrailingExits()
    {
        // Default 5%/3% rarely arms on this oscillator; an ultra-tight trail
        // must arm quickly and convert exits into Trailing ones.
        var tight = await HistoricBacktester.RunAsync(
            Market(), Config() with { TrailingActivationPct = 0.005m, TrailingDistancePct = 0.005m });

        tight.TradeLog.Should().Contain(t => t.ExitReason.StartsWith("Trailing"));
    }

    [Fact]
    public async Task MaxOpenPositionsOverride_CapsConcurrency()
    {
        var single = await HistoricBacktester.RunAsync(Market(), Config() with { MaxOpenPositions = 1 });

        // Reconstruct concurrency from the trade log: at no point may two
        // positions overlap when the cap is 1.
        foreach (var t in single.TradeLog)
        {
            single.TradeLog.Count(o => o != t && o.EntryDate < t.ExitDate && o.ExitDate > t.EntryDate)
                .Should().Be(0);
        }
    }
}
