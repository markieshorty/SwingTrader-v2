using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class UserRepositoryAdminActionsTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<SwingTraderDbContext> SeedUserAsync(string userId = "u1")
    {
        var db = CreateDb();
        db.AppUsers.Add(new AppUser
        {
            UserId = userId,
            Email = "u@example.com",
            DisplayName = "U",
            AccountId = 1,
            Role = AccountRole.Owner,
            IsOnboarded = true,
            OnboardingStep = 3,
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task SuspendAsync_SetsSuspendedFieldsWithReason()
    {
        await using var db = await SeedUserAsync();
        var repo = new UserRepository(db);

        await repo.SuspendAsync("u1", "Terms of service violation");

        var user = await db.AppUsers.SingleAsync();
        user.IsSuspended.Should().BeTrue();
        user.SuspendedAt.Should().NotBeNull();
        user.SuspendReason.Should().Be("Terms of service violation");
    }

    [Fact]
    public async Task UnsuspendAsync_ClearsAllSuspensionFields()
    {
        await using var db = await SeedUserAsync();
        var repo = new UserRepository(db);
        await repo.SuspendAsync("u1", "reason");

        await repo.UnsuspendAsync("u1");

        var user = await db.AppUsers.SingleAsync();
        user.IsSuspended.Should().BeFalse();
        user.SuspendedAt.Should().BeNull();
        user.SuspendReason.Should().BeNull();
    }

    [Fact]
    public async Task ResetOnboardingAsync_ResetsFlagAndStep()
    {
        await using var db = await SeedUserAsync();
        var repo = new UserRepository(db);

        await repo.ResetOnboardingAsync("u1");

        var user = await db.AppUsers.SingleAsync();
        user.IsOnboarded.Should().BeFalse();
        user.OnboardingStep.Should().Be(0);
    }

    [Fact]
    public async Task SuspendAsync_UnknownUser_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new UserRepository(db);

        var act = async () => await repo.SuspendAsync("nobody", "reason");

        await act.Should().NotThrowAsync();
    }
}
