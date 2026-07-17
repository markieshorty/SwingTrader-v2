using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class SetupTacticsRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // Seeds the account's regime books first (setup tactics copy the Neutral one).
    private static async Task<SetupTacticsRepository> SeededRepoAsync(SwingTraderDbContext db)
    {
        await new AccountRiskProfileRepository(db).SeedDefaultAsync(1);
        var repo = new SetupTacticsRepository(db);
        await repo.SeedDefaultAsync(1);
        return repo;
    }

    [Fact]
    public async Task SeedDefaultAsync_CopiesTheNeutralBook_ForAllTradableSetups()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        var all = await repo.GetAllAsync(1);

        all.Should().HaveCount(6);
        all.Select(t => t.SetupType).Should().BeEquivalentTo(new[]
        {
            SetupType.OversoldRecovery, SetupType.OversoldRecoveryLoose, SetupType.Breakout,
            SetupType.MomentumContinuation, SetupType.VolumeSpike, SetupType.TrendFollowing,
        });
        // Continuity: every setup starts identical to the Neutral book's defaults.
        all.Should().OnlyContain(t => t.StopLossPct == 0.05m && t.TargetPct == 0.08m && t.GuideHoldDays == 10);
    }

    [Fact]
    public async Task SeedDefaultAsync_SeedsLooseVariantDisabled_OthersEnabled()
    {
        // The unconfirmed (loose) oversold variant must never start trading
        // without the owner explicitly switching it on.
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        var all = await repo.GetAllAsync(1);

        all.Single(t => t.SetupType == SetupType.OversoldRecoveryLoose).Enabled.Should().BeFalse();
        all.Where(t => t.SetupType != SetupType.OversoldRecoveryLoose).Should().OnlyContain(t => t.Enabled);
    }

    [Fact]
    public async Task SeedDefaultAsync_LooseVariantInheritsConfirmedSetupTactics_WhenAlreadyDifferentiated()
    {
        // An account that tuned OversoldRecovery BEFORE the split gets a Loose
        // row copying those tuned values (they were one setup until 17 Jul
        // 2026), not the Neutral-book defaults.
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);
        var confirmed = await repo.GetAsync(1, SetupType.OversoldRecovery);
        confirmed!.StopLossPct = 0.03m;
        confirmed.TargetPct = 0.15m;
        confirmed.GuideHoldDays = 7;
        await repo.UpdateAsync(confirmed);
        db.SetupTactics.RemoveRange(db.SetupTactics.Where(t => t.SetupType == SetupType.OversoldRecoveryLoose));
        await db.SaveChangesAsync();

        await repo.SeedDefaultAsync(1); // reseeds the missing Loose row

        var loose = await repo.GetAsync(1, SetupType.OversoldRecoveryLoose);
        loose!.StopLossPct.Should().Be(0.03m);
        loose.TargetPct.Should().Be(0.15m);
        loose.GuideHoldDays.Should().Be(7);
        loose.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SeedDefaultAsync_IsIdempotent()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        await repo.SeedDefaultAsync(1);

        (await db.SetupTactics.CountAsync(t => t.AccountId == 1)).Should().Be(6);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheRequestedSetup()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        var breakout = await repo.GetAsync(1, SetupType.Breakout);

        breakout.Should().NotBeNull();
        breakout!.SetupType.Should().Be(SetupType.Breakout);
    }

    [Fact]
    public async Task UpdateAsync_ValidChange_Persists()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);
        var t = await repo.GetAsync(1, SetupType.Breakout);
        t!.StopLossPct = 0.09m;
        t.TargetPct = 0.25m;
        t.GuideHoldDays = 20;

        await repo.UpdateAsync(t);

        var updated = await repo.GetAsync(1, SetupType.Breakout);
        updated!.StopLossPct.Should().Be(0.09m);
        updated.TargetPct.Should().Be(0.25m);
        updated.GuideHoldDays.Should().Be(20);
    }

    [Fact]
    public async Task SeededSetups_AreEnabledByDefault_ExceptTheLooseVariant()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        (await repo.GetAllAsync(1))
            .Where(t => t.SetupType != SetupType.OversoldRecoveryLoose)
            .Should().OnlyContain(t => t.Enabled);
        // The unconfirmed oversold variant deliberately seeds OFF.
        (await repo.GetDisabledSetupsAsync(1)).Should().BeEquivalentTo(new[] { SetupType.OversoldRecoveryLoose });
    }

    [Fact]
    public async Task UpdateAsync_DisablingASetup_PersistsAndSurfacesInDisabledSet()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);
        var t = await repo.GetAsync(1, SetupType.Breakout);
        t!.Enabled = false;

        await repo.UpdateAsync(t);

        (await repo.GetAsync(1, SetupType.Breakout))!.Enabled.Should().BeFalse();
        // Loose seeds disabled, so the set is Breakout + the loose variant.
        (await repo.GetDisabledSetupsAsync(1)).Should().BeEquivalentTo(
            new[] { SetupType.Breakout, SetupType.OversoldRecoveryLoose });
        // Other setups stay enabled.
        (await repo.GetAsync(1, SetupType.OversoldRecovery))!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_TargetNotAboveStop_Throws()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);
        var t = await repo.GetAsync(1, SetupType.Breakout);
        t!.StopLossPct = 0.10m;
        t.TargetPct = 0.08m;

        var act = () => repo.UpdateAsync(t);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Target must exceed*");
    }
}
