using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class CandleRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StockCandle Candle(string symbol, DateTime ts, string resolution = "D") =>
        new() { Symbol = symbol, Timestamp = ts, Resolution = resolution, Open = 1, High = 2, Low = 0.5m, Close = 1.5m, Volume = 1000 };

    [Fact]
    public async Task SaveCandlesAsync_DuplicateSymbolResolutionTimestamp_IsNotAddedTwice()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);
        var ts = new DateTime(2026, 7, 1);
        await repo.SaveCandlesAsync(1, [Candle("AAPL", ts)]);

        await repo.SaveCandlesAsync(1, [Candle("AAPL", ts)]);

        db.StockCandles.Count(c => c.Symbol == "AAPL").Should().Be(1);
    }

    [Fact]
    public async Task GetCandlesAsync_FiltersBySymbolResolutionAndDateRange_UppercasesSymbol()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);
        await repo.SaveCandlesAsync(1, [
            Candle("AAPL", new DateTime(2026, 6, 1)),
            Candle("AAPL", new DateTime(2026, 7, 1)),
            Candle("MSFT", new DateTime(2026, 7, 1)),
        ]);

        var result = await repo.GetCandlesAsync("aapl", "D", new DateTime(2026, 6, 15), new DateTime(2026, 7, 15));

        result.Should().ContainSingle();
        result[0].Timestamp.Should().Be(new DateTime(2026, 7, 1));
    }

    [Fact]
    public async Task GetLatestCandleDateAsync_ReturnsMostRecentTimestamp()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);
        await repo.SaveCandlesAsync(1, [Candle("AAPL", new DateTime(2026, 6, 1)), Candle("AAPL", new DateTime(2026, 7, 1))]);

        var result = await repo.GetLatestCandleDateAsync("AAPL", "D");

        result.Should().Be(new DateTime(2026, 7, 1));
    }

    [Fact]
    public async Task GetLatestCandleDateAsync_NoCandles_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);

        var result = await repo.GetLatestCandleDateAsync("AAPL", "D");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestCandleDatesAsync_ReturnsOneEntryPerSymbol()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);
        await repo.SaveCandlesAsync(1, [
            Candle("AAPL", new DateTime(2026, 6, 1)),
            Candle("AAPL", new DateTime(2026, 7, 1)),
            Candle("MSFT", new DateTime(2026, 7, 2)),
        ]);

        var result = await repo.GetLatestCandleDatesAsync(["aapl", "msft"], "D");

        result["AAPL"].Should().Be(new DateTime(2026, 7, 1));
        result["MSFT"].Should().Be(new DateTime(2026, 7, 2));
    }

    [Fact]
    public async Task GetCandlesForSymbolsAsync_EmptySymbolList_ReturnsEmptyDictionary()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);

        var result = await repo.GetCandlesForSymbolsAsync([], "D", DateTime.MinValue, DateTime.MaxValue);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCandlesForSymbolsAsync_GroupsBySymbolWithinRange()
    {
        await using var db = CreateDb();
        var repo = new CandleRepository(db);
        await repo.SaveCandlesAsync(1, [
            Candle("AAPL", new DateTime(2026, 7, 1)),
            Candle("MSFT", new DateTime(2026, 7, 1)),
        ]);

        var result = await repo.GetCandlesForSymbolsAsync(["AAPL", "MSFT"], "D", new DateTime(2026, 6, 1), new DateTime(2026, 7, 31));

        result.Should().ContainKeys("AAPL", "MSFT");
        result["AAPL"].Should().ContainSingle();
    }
}
