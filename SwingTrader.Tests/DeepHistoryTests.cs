using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// Deep history P1 (docs/deep-history-plan): the per-request data window.
// Null = the standard 2016+ window with byte-identical behaviour and
// fingerprints; an explicit earlier year filters the load and hashes.
public class DeepHistoryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(2016, null)] // explicit standard year normalizes to null
    [InlineData(2000, 2000)]
    [InlineData(2010, 2010)]
    public void Normalize_TreatsStandardYearAsNull(int? requested, int? expected) =>
        HistoricDataWindow.Normalize(requested).Should().Be(expected);

    [Fact]
    public void Fingerprint_UnchangedForStandardWindow_ChangedForDeepWindow()
    {
        var baseCfg = new HistoricConfig(new StrategyWeights());

        var plain = ConfigFingerprint.Compute(baseCfg);
        var explicitStandard = ConfigFingerprint.Compute(baseCfg with { DataFromYear = 2016 });
        var deep = ConfigFingerprint.Compute(baseCfg with { DataFromYear = 2000 });

        // Null and explicit-2016 are the SAME config - and both must match
        // every fingerprint stamped before this field existed.
        explicitStandard.Should().Be(plain);
        deep.Should().NotBe(plain);
    }

    private static List<HistoricalCandle> Series(int days, Func<int, decimal> close, Func<int, bool>? skip = null)
    {
        var bars = new List<HistoricalCandle>();
        var date = new DateOnly(2003, 1, 6); // a Monday
        var added = 0;
        for (var i = 0; added < days; i++)
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { date = date.AddDays(1); continue; }
            if (skip?.Invoke(i) != true)
            {
                bars.Add(new HistoricalCandle
                {
                    Symbol = "X", Date = date,
                    Open = close(added), High = close(added), Low = close(added), Close = close(added), Volume = 1_000_000m,
                });
                added++;
            }
            date = date.AddDays(1);
        }
        return bars;
    }

    [Fact]
    public void QualityGates_CleanDailySeries_Passes()
    {
        var clean = Series(300, i => 20m + i * 0.01m);
        DelistedBackfillService.IsTooPatchy(clean).Should().BeFalse();
        DelistedBackfillService.HasSplitArtifact(clean).Should().BeFalse();
    }

    [Fact]
    public void IsTooPatchy_RejectsSeriesMissingOverTenPercentOfDays()
    {
        // Every 4th weekday missing -> ~25% of trading days absent.
        var patchy = Series(300, i => 20m, skip: i => i % 4 == 0);
        DelistedBackfillService.IsTooPatchy(patchy).Should().BeTrue();
    }

    [Fact]
    public void HasSplitArtifact_CatchesUnadjustedSplitJump()
    {
        var split = Series(100, i => i == 50 ? 120m : 20m); // 6x up then back
        DelistedBackfillService.HasSplitArtifact(split).Should().BeTrue();
    }

    [Fact]
    public async Task SqlRepository_FromFilter_DropsOlderBars()
    {
        await using var db = new SwingTraderDbContext(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var repo = new HistoricalCandleRepository(db);
        HistoricalCandle Bar(string s, int year) => new()
        {
            Symbol = s,
            Date = new DateOnly(year, 6, 1),
            Open = 1m, High = 2m, Low = 1m, Close = 1.5m, Volume = 100m,
        };
        await repo.AddRangeAsync([Bar("OLD", 2005), Bar("BOTH", 2005), Bar("BOTH", 2020), Bar("NEW", 2020)]);

        var windowed = await repo.GetAllBySymbolAsync(new DateOnly(2016, 1, 1));
        var everything = await repo.GetAllBySymbolAsync();

        windowed.Should().NotContainKey("OLD");
        windowed["BOTH"].Should().HaveCount(1);
        windowed["BOTH"][0].Date.Year.Should().Be(2020);
        windowed["NEW"].Should().HaveCount(1);
        everything["BOTH"].Should().HaveCount(2);
        everything["OLD"].Should().HaveCount(1);
    }
}
