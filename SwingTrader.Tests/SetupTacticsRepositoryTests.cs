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
    public async Task SeedDefaultAsync_CopiesTheNeutralBook_ForAllFiveTradableSetups()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        var all = await repo.GetAllAsync(1);

        all.Should().HaveCount(5);
        all.Select(t => t.SetupType).Should().BeEquivalentTo(new[]
        {
            SetupType.OversoldRecovery, SetupType.Breakout, SetupType.MomentumContinuation,
            SetupType.VolumeSpike, SetupType.TrendFollowing,
        });
        // Continuity: every setup starts identical to the Neutral book's defaults.
        all.Should().OnlyContain(t => t.StopLossPct == 0.05m && t.TargetPct == 0.08m && t.GuideHoldDays == 10);
    }

    [Fact]
    public async Task SeedDefaultAsync_IsIdempotent()
    {
        await using var db = CreateDb();
        var repo = await SeededRepoAsync(db);

        await repo.SeedDefaultAsync(1);

        (await db.SetupTactics.CountAsync(t => t.AccountId == 1)).Should().Be(5);
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
