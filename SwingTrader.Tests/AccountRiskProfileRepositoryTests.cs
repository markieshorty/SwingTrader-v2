using SwingTrader.Core.Constants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace SwingTrader.Tests;

public class AccountRiskProfileRepositoryTests
{
    private static SwingTraderDbContext CreateDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task SeedDefaultAsync_CreatesRowWithDefaults()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);

        await repo.SeedDefaultAsync(1);

        var profile = await db.AccountRiskProfiles.SingleAsync(p => p.AccountId == 1 && p.Regime == MarketRegime.Neutral);
        profile.LockedCapitalPct.Should().Be(CapitalRules.LockedCapitalPct);
        profile.MaxOpenPositions.Should().Be(3);
    }

    [Fact]
    public async Task SeedDefaultAsync_AlreadySeeded_DoesNotDuplicate()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);

        await repo.SeedDefaultAsync(1);
        await repo.SeedDefaultAsync(1);

        // Four regime books, seeded once - the second call adds nothing.
        (await db.AccountRiskProfiles.CountAsync(p => p.AccountId == 1)).Should().Be(5); // Default + 4 regime books
    }

    [Fact]
    public async Task GetAsync_NoExistingProfile_SeedsAndReturnsDefault()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);

        var profile = await repo.GetAsync(1);

        profile.AccountId.Should().Be(1);
        profile.LockedCapitalPct.Should().Be(CapitalRules.LockedCapitalPct);
        (await db.AccountRiskProfiles.CountAsync()).Should().Be(5); // Default + 4 regime books
    }

    [Fact]
    public async Task GetAsync_ExistingProfile_ReturnsIt()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);
        await repo.SeedDefaultAsync(1);
        var seeded = await db.AccountRiskProfiles.SingleAsync(p => p.AccountId == 1 && p.Regime == MarketRegime.Neutral);
        seeded.MaxOpenPositions = 5;
        await db.SaveChangesAsync();

        var profile = await repo.GetAsync(1);

        profile.MaxOpenPositions.Should().Be(5);
    }

    [Fact]
    public async Task UpdateAsync_ValidChanges_Persists()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);
        await repo.SeedDefaultAsync(1);
        var profile = await repo.GetAsync(1);
        profile.LockedCapitalPct = 0.60m;
        profile.FlatPositionPct = 0.12m;

        await repo.UpdateAsync(profile);

        var updated = await db.AccountRiskProfiles.SingleAsync(p => p.AccountId == 1 && p.Regime == MarketRegime.Neutral);
        updated.LockedCapitalPct.Should().Be(0.60m);
        updated.FlatPositionPct.Should().Be(0.12m);
    }

    [Fact]
    public async Task UpdateAsync_InvalidChanges_ThrowsAndDoesNotPersist()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seedDb = CreateDb(dbName))
            await new AccountRiskProfileRepository(seedDb).SeedDefaultAsync(1);

        await using (var updateDb = CreateDb(dbName))
        {
            var repo = new AccountRiskProfileRepository(updateDb);
            var profile = await repo.GetAsync(1);
            profile.LockedCapitalPct = 0.95m; // above the 90% ceiling

            var act = async () => await repo.UpdateAsync(profile);

            await act.Should().ThrowAsync<ValidationException>();
        }

        await using var verifyDb = CreateDb(dbName);
        var stored = await verifyDb.AccountRiskProfiles.SingleAsync(p => p.AccountId == 1 && p.Regime == MarketRegime.Neutral);
        stored.LockedCapitalPct.Should().Be(CapitalRules.LockedCapitalPct);
    }

    [Fact]
    public async Task UpdateAsync_NoExistingProfile_Throws()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);
        var profile = new SwingTrader.Core.Models.AccountRiskProfile { AccountId = 99 };

        var act = async () => await repo.UpdateAsync(profile);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResetToDefaultsAsync_ExistingModifiedProfile_RestoresDefaults()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);
        await repo.SeedDefaultAsync(1);
        var profile = await repo.GetAsync(1);
        profile.LockedCapitalPct = 0.55m;
        profile.MaxOpenPositions = 4; // 4 x 10% = 40% <= 45% un-locked
        await repo.UpdateAsync(profile);

        var reset = await repo.ResetToDefaultsAsync(1, MarketRegime.Neutral);

        reset.LockedCapitalPct.Should().Be(CapitalRules.LockedCapitalPct);
        reset.MaxOpenPositions.Should().Be(3);
    }

    [Fact]
    public async Task UpdateAsync_PersistsTheFunnelDials()
    {
        // Regression: the field-by-field copy in UpdateAsync originally missed
        // SizingAggressiveness and ForwardVetoFloor, so the Settings sliders
        // saved successfully while silently persisting nothing.
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);
        await repo.SeedDefaultAsync(1);
        var profile = await repo.GetAsync(1);
        profile.SizingAggressiveness = 0.7m;
        profile.ForwardVetoFloor = 3.5m;

        await repo.UpdateAsync(profile);

        var updated = await db.AccountRiskProfiles.SingleAsync(p => p.AccountId == 1 && p.Regime == MarketRegime.Neutral);
        updated.SizingAggressiveness.Should().Be(0.7m);
        updated.ForwardVetoFloor.Should().Be(3.5m);
    }

    [Fact]
    public async Task ResetToDefaultsAsync_RestoresTheFunnelDials()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);
        await repo.SeedDefaultAsync(1);
        var profile = await repo.GetAsync(1);
        profile.SizingAggressiveness = 1.0m;
        profile.ForwardVetoFloor = 0.0m;
        profile.AutopauseTrading = true;
        await repo.UpdateAsync(profile);

        var reset = await repo.ResetToDefaultsAsync(1, MarketRegime.Neutral);

        reset.SizingAggressiveness.Should().Be(0m);
        reset.ForwardVetoFloor.Should().Be(CapitalRules.DefaultForwardVetoFloor);
        // Neutral's default posture does not auto-pause.
        reset.AutopauseTrading.Should().BeFalse();
    }

    [Fact]
    public async Task ResetToDefaultsAsync_NoExistingProfile_SeedsDefault()
    {
        await using var db = CreateDb();
        var repo = new AccountRiskProfileRepository(db);

        var reset = await repo.ResetToDefaultsAsync(1, MarketRegime.Neutral);

        reset.LockedCapitalPct.Should().Be(CapitalRules.LockedCapitalPct);
        (await db.AccountRiskProfiles.CountAsync()).Should().Be(5); // Default + 4 regime books
    }
}
