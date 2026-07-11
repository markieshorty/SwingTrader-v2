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

    // Threshold 3 = take everything scoreable; probation off by default so
    // each rule test isolates its own lever (the probation tests turn it on).
    private static HistoricConfig Config() => new(new StrategyWeights(), BuyThreshold: 3.0m, SimulateProbation: false);

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
    public async Task TargetOverride_TightTarget_ProducesNearTargetExits()
    {
        // Flat +2% target on a ±10% oscillator: target exits must appear and
        // no winning trade can bank much more than the target (gap fills at
        // the open can exceed it slightly; costs shave both sides).
        var tight = await HistoricBacktester.RunAsync(
            Market(), Config() with { TargetPct = 0.02m });

        tight.TradeLog.Should().Contain(t => t.ExitReason == "Target");
        tight.TradeLog.Where(t => t.ExitReason == "Target")
            .Should().AllSatisfy(t => t.ReturnPct.Should().BeLessThan(3m));
    }

    [Fact]
    public async Task StopOverride_TightStop_BoundsLosses()
    {
        var tight = await HistoricBacktester.RunAsync(
            Market(), Config() with { StopLossPct = 0.02m });

        tight.TradeLog.Should().Contain(t => t.ExitReason == "StopLoss");
        // Non-gap stop exits fill at the stop level: loss ≈ 2% + round-trip costs.
        tight.TradeLog.Where(t => t.ExitReason == "StopLoss")
            .Should().AllSatisfy(t => t.ReturnPct.Should().BeGreaterThan(-3.5m));
    }

    [Fact]
    public async Task DefaultStopTarget_MatchesExplicitProfileDefaults()
    {
        // Config defaults (5%/8%) must equal spelling the same values out -
        // pins that the engine's defaults track CapitalRules defaults.
        var defaults = await HistoricBacktester.RunAsync(Market(), Config());
        var explicit58 = await HistoricBacktester.RunAsync(
            Market(), Config() with { StopLossPct = 0.05m, TargetPct = 0.08m });

        explicit58.Trades.Should().Be(defaults.Trades);
        explicit58.ExpectancyPct.Should().Be(defaults.ExpectancyPct);
    }

    [Fact]
    public async Task Probation_On_ProducesProbationExits_OffDoesNot()
    {
        // The oscillator guarantees some entries near a peak: three days later
        // price is below entry with falling RSI - a legitimate Exit verdict.
        var withProbation = await HistoricBacktester.RunAsync(
            Market(), Config() with { SimulateProbation = true, MinHoldDays = 3 });
        var without = await HistoricBacktester.RunAsync(Market(), Config());

        withProbation.TradeLog.Should().Contain(t => t.ExitReason == "Probation");
        without.TradeLog.Should().NotContain(t => t.ExitReason == "Probation");
    }

    [Fact]
    public async Task Probation_ImpossibleThreshold_ExitsEverySurvivorAtCheckDay()
    {
        // Threshold above the max possible score (1.0): every position that
        // reaches the check day must exit on probation - nothing survives to
        // a time exit at MaxHoldDays 10.
        var result = await HistoricBacktester.RunAsync(
            Market(), Config() with { SimulateProbation = true, MinHoldDays = 3, MomentumHealthThreshold = 1.01m });

        result.TradeLog.Should().NotContain(t => t.ExitReason == "TimeExit");
        result.TradeLog.Should().Contain(t => t.ExitReason == "Probation");
    }

    [Fact]
    public async Task PoolSizing_BoundsExposure_TinyPoolTakesNoTradesAtAll()
    {
        // Live-mirroring pool sizing. Tier-1-style pool (10% of £10k equity,
        // 33% per position => ~£330 budgets) still trades...
        var tier1 = await HistoricBacktester.RunAsync(
            Market(), Config() with { ActiveCapitalPct = 0.10m, MaxPositionPctOfActive = 0.33m });
        tier1.Trades.Should().BeGreaterThan(0);

        // ...but a pool so small that per-position budgets fall under the £50
        // dust guard (1.5% pool x 33% = ~£49) must take ZERO trades - proof
        // the pool, not free cash, is what bounds deployment.
        var tiny = await HistoricBacktester.RunAsync(
            Market(), Config() with { ActiveCapitalPct = 0.015m, MaxPositionPctOfActive = 0.33m });
        tiny.Trades.Should().Be(0);

        // Pool-sized trades move a small slice of equity, so the equity curve
        // must swing far less than the flat-10% run's.
        var flat = await HistoricBacktester.RunAsync(Market(), Config());
        Math.Abs(tier1.TotalReturnPct).Should().BeLessThan(Math.Abs(flat.TotalReturnPct));
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
