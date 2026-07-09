using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class AccountInviteRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task FindValidByTokenAsync_ValidUnexpiredUnaccepted_IsReturned()
    {
        await using var db = CreateDb();
        var repo = new AccountInviteRepository(db);
        await repo.CreateAsync(new AccountInvite { AccountId = 1, Token = "abc", ExpiresAt = DateTime.UtcNow.AddDays(1) });

        var result = await repo.FindValidByTokenAsync("abc");

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindValidByTokenAsync_ExpiredInvite_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new AccountInviteRepository(db);
        await repo.CreateAsync(new AccountInvite { AccountId = 1, Token = "abc", ExpiresAt = DateTime.UtcNow.AddDays(-1) });

        var result = await repo.FindValidByTokenAsync("abc");

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindValidByTokenAsync_AlreadyAccepted_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new AccountInviteRepository(db);
        await repo.CreateAsync(new AccountInvite { AccountId = 1, Token = "abc", ExpiresAt = DateTime.UtcNow.AddDays(1), AcceptedAt = DateTime.UtcNow });

        var result = await repo.FindValidByTokenAsync("abc");

        result.Should().BeNull();
    }

    [Fact]
    public async Task MarkAcceptedAsync_SetsAcceptedFields()
    {
        await using var db = CreateDb();
        var repo = new AccountInviteRepository(db);
        var invite = await repo.CreateAsync(new AccountInvite { AccountId = 1, Token = "abc", ExpiresAt = DateTime.UtcNow.AddDays(1) });

        await repo.MarkAcceptedAsync(invite.Id, "user-42");

        var reloaded = await repo.FindValidByTokenAsync("abc");
        reloaded.Should().BeNull(); // now accepted, no longer "valid"
        db.AccountInvites.First(i => i.Id == invite.Id).AcceptedByUserId.Should().Be("user-42");
    }

    [Fact]
    public async Task MarkAcceptedAsync_UnknownId_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new AccountInviteRepository(db);

        var act = async () => await repo.MarkAcceptedAsync(999, "user-42");

        await act.Should().NotThrowAsync();
    }
}
