using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// GetAllBySymbolAsync loads the whole candle table in four sequential symbol-
// partitioned reads (the single-query version outgrew the 300s command timeout
// on the Basic tier, 14 Jul 2026). These pin that the assembled result is
// complete and correctly ordered regardless of how the symbols partition.
public class HistoricalCandleRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HistoricalCandle Candle(string symbol, DateOnly date) => new()
    {
        Symbol = symbol,
        Date = date,
        Open = 1m, High = 2m, Low = 1m, Close = 1.5m, Volume = 100m,
    };

    [Theory]
    [InlineData(1)]    // fewer symbols than partitions
    [InlineData(4)]    // exactly the partition count
    [InlineData(10)]   // uneven split across 4 partitions
    [InlineData(37)]   // larger uneven split
    public async Task GetAllBySymbolAsync_ReturnsEverySymbol_AcrossPartitions(int symbolCount)
    {
        await using var db = CreateDb();
        var repo = new HistoricalCandleRepository(db);

        var expected = new Dictionary<string, int>();
        var toAdd = new List<HistoricalCandle>();
        for (var s = 0; s < symbolCount; s++)
        {
            var symbol = $"SYM{s:D3}";
            var bars = 3 + (s % 5); // varying history length per symbol
            expected[symbol] = bars;
            for (var d = 0; d < bars; d++)
                toAdd.Add(Candle(symbol, new DateOnly(2026, 1, 1).AddDays(d)));
        }
        await repo.AddRangeAsync(toAdd);

        var result = await repo.GetAllBySymbolAsync();

        // Every symbol present, with its full (correct-length) history.
        result.Should().HaveCount(symbolCount);
        foreach (var (symbol, bars) in expected)
            result[symbol].Should().HaveCount(bars, $"symbol {symbol} should keep all its bars");
    }

    [Fact]
    public async Task GetAllBySymbolAsync_OrdersEachSymbolsCandlesByDate()
    {
        await using var db = CreateDb();
        var repo = new HistoricalCandleRepository(db);

        // Insert out of date order to prove the read sorts within a symbol.
        await repo.AddRangeAsync(new[]
        {
            Candle("AAA", new DateOnly(2026, 1, 3)),
            Candle("AAA", new DateOnly(2026, 1, 1)),
            Candle("AAA", new DateOnly(2026, 1, 2)),
        });

        var result = await repo.GetAllBySymbolAsync();

        result["AAA"].Select(c => c.Date).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetAllBySymbolAsync_EmptyTable_ReturnsEmpty()
    {
        await using var db = CreateDb();
        var result = await new HistoricalCandleRepository(db).GetAllBySymbolAsync();
        result.Should().BeEmpty();
    }
}
