using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Agents.Monitor;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class MomentumHealthServiceTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Trade OpenTrade(int accountId = 1, decimal entryPrice = 100m, int? signalId = null) => new()
    {
        AccountId = accountId,
        Symbol = "TEST",
        EntryPrice = entryPrice,
        Quantity = 10,
        StopLossPrice = 95m,
        TargetPrice = 110m,
        OpenedAt = DateTime.UtcNow.AddDays(-3),
        SignalId = signalId,
    };

    private static (MomentumHealthService Service, SwingTraderDbContext Db) CreateService()
    {
        var db = CreateDb();
        var signalRepo = new SignalRepository(db);
        var riskProfileRepo = new AccountRiskProfileRepository(db);
        return (new MomentumHealthService(signalRepo, riskProfileRepo), db);
    }

    [Fact]
    public async Task CalculateAsync_NoSignalToday_ReturnsNeutralBorderline()
    {
        var (service, db) = CreateService();
        var trade = OpenTrade();

        var result = await service.CalculateAsync(1, trade);

        result.Score.Should().Be(0.50m);
        result.Verdict.Should().Be("Borderline");
        result.Reasoning.Should().Contain("Insufficient data");
    }

    [Fact]
    public async Task CalculateAsync_AllPositive_ReturnsConfirmed()
    {
        var (service, db) = CreateService();

        var entrySignal = new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
            CurrentPrice = 100m,
            Rsi14 = 40m,
        };
        db.StockSignals.Add(entrySignal);
        await db.SaveChangesAsync();

        var trade = OpenTrade(entryPrice: 100m, signalId: entrySignal.Id);

        db.StockSignals.Add(new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentPrice = 102.5m, // +2.5% from entry
            Rsi14 = 60m, // rising vs entry's 40, and >= 50 -> rsiScore 1.0
            VolumeRatio = 1.2m,
            RelativeReturn = 1.0m,
        });
        await db.SaveChangesAsync();

        var result = await service.CalculateAsync(1, trade);

        result.Score.Should().Be(1.00m);
        result.Verdict.Should().Be("Confirmed");
    }

    [Fact]
    public async Task CalculateAsync_AllNegative_ReturnsExit()
    {
        var (service, db) = CreateService();
        var trade = OpenTrade(entryPrice: 100m);

        db.StockSignals.Add(new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentPrice = 98.8m, // -1.2% from entry
            Rsi14 = 30m, // falling relative to entry (no entry signal — falls back to itself, so "rising" is false only if computed vs entry; here no entrySignal so entryRsi==rsi, rising==false)
            VolumeRatio = 0.4m,
            RelativeReturn = -1.0m,
        });
        await db.SaveChangesAsync();

        var result = await service.CalculateAsync(1, trade);

        result.Score.Should().Be(0.00m);
        result.Verdict.Should().Be("Exit");
    }

    [Fact]
    public async Task CalculateAsync_MixedFlatSignals_ReturnsBorderlineAtDefaultThreshold()
    {
        var (service, db) = CreateService();
        var trade = OpenTrade(entryPrice: 100m);

        db.StockSignals.Add(new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentPrice = 100.3m, // +0.3%, between 0 and 1.5 -> priceScore 0.5
            Rsi14 = 45m, // no entry signal so entryRsi==rsi -> rising=false -> rsiScore 0 (falling branch)
            VolumeRatio = 0.6m, // 0.5-0.8 -> 0.5
            RelativeReturn = 0m, // between -0.5 and 0.5 -> 0.5
        });
        await db.SaveChangesAsync();

        var result = await service.CalculateAsync(1, trade);

        // rsi 0*0.30 + volume 0.5*0.25 + price 0.5*0.25 + relative 0.5*0.20 = 0.35
        result.Score.Should().Be(0.35m);
        result.Verdict.Should().Be("Borderline"); // default threshold 0.35, 0.35 >= 0.35 -> Borderline (not Confirmed, needs +0.25)
    }

    [Fact]
    public async Task CalculateAsync_UsesAccountConfiguredThreshold()
    {
        var (service, db) = CreateService();
        await db.AccountRiskProfiles.AddAsync(new AccountRiskProfile { AccountId = 1, MomentumHealthThreshold = 0.50m });
        await db.SaveChangesAsync();

        var trade = OpenTrade(entryPrice: 100m);
        db.StockSignals.Add(new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentPrice = 100.3m,
            Rsi14 = 45m,
            VolumeRatio = 0.6m,
            RelativeReturn = 0m,
        });
        await db.SaveChangesAsync();

        var result = await service.CalculateAsync(1, trade);

        // Same 0.35 score, but a 0.50 threshold now fails it.
        result.Score.Should().Be(0.35m);
        result.Verdict.Should().Be("Exit");
    }

    [Fact]
    public async Task CalculateAsync_RsiRisingRelativeToEntrySignal_ScoresHigher()
    {
        var (service, db) = CreateService();

        var entrySignal = new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
            CurrentPrice = 100m,
            Rsi14 = 40m,
        };
        db.StockSignals.Add(entrySignal);
        await db.SaveChangesAsync();

        var trade = OpenTrade(entryPrice: 100m, signalId: entrySignal.Id);

        db.StockSignals.Add(new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentPrice = 100.3m,
            Rsi14 = 55m, // rising vs entry's 40, and >= 50 -> rsiScore 1.0
            VolumeRatio = 0.6m,
            RelativeReturn = 0m,
        });
        await db.SaveChangesAsync();

        var result = await service.CalculateAsync(1, trade);

        result.RsiDirectionScore.Should().Be(1.00m);
        result.Reasoning.Should().Contain("RSI rising above 50");
    }

    [Fact]
    public async Task CalculateAsync_MissingIndicatorFields_TreatsAsNeutral()
    {
        var (service, db) = CreateService();
        var trade = OpenTrade(entryPrice: 100m);

        db.StockSignals.Add(new StockSignal
        {
            AccountId = 1,
            Symbol = "TEST",
            SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentPrice = 100m,
            // Rsi14, VolumeRatio, RelativeReturn all left null
        });
        await db.SaveChangesAsync();

        var result = await service.CalculateAsync(1, trade);

        result.RsiDirectionScore.Should().Be(0.50m);
        result.VolumeScore.Should().Be(0.50m);
        result.RelativeStrengthScore.Should().Be(0.50m);
    }
}
