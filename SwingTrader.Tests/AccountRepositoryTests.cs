using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class AccountRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ListActiveAsync_ExcludesAccountsWithNoAppUser()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, Name = "system" }); // no AppUser - e.g. the seed account
        db.Accounts.Add(new Account { Id = 2, Name = "Real Account" });
        db.AppUsers.Add(new AppUser { UserId = "u1", Email = "u1@example.com", DisplayName = "U1", AccountId = 2, Role = AccountRole.Owner });
        await db.SaveChangesAsync();
        var repo = new AccountRepository(db);

        var active = await repo.ListActiveAsync();

        active.Should().ContainSingle(a => a.Id == 2);
        active.Should().NotContain(a => a.Id == 1);
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesSoftDeletedAccountsEvenWithAnOwner()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, Name = "Deleted Account", IsDeleted = true });
        db.AppUsers.Add(new AppUser { UserId = "u1", Email = "u1@example.com", DisplayName = "U1", AccountId = 1, Role = AccountRole.Owner });
        await db.SaveChangesAsync();
        var repo = new AccountRepository(db);

        var active = await repo.ListActiveAsync();

        active.Should().BeEmpty();
    }

    [Fact]
    public async Task ListActiveAsync_IncludesAccountWithMemberOnly()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, Name = "Account" });
        db.AppUsers.Add(new AppUser { UserId = "u1", Email = "u1@example.com", DisplayName = "U1", AccountId = 1, Role = AccountRole.Member });
        await db.SaveChangesAsync();
        var repo = new AccountRepository(db);

        var active = await repo.ListActiveAsync();

        active.Should().ContainSingle(a => a.Id == 1);
    }
}
