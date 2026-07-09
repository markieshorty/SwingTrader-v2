using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class NotificationRecipientRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ListAsync_ScopedToAccount()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        await repo.AddAsync(new NotificationRecipient { AccountId = 1, Email = "a@x.com" });
        await repo.AddAsync(new NotificationRecipient { AccountId = 2, Email = "b@x.com" });

        var result = await repo.ListAsync(1);

        result.Should().ContainSingle(r => r.Email == "a@x.com");
    }

    [Fact]
    public async Task RemoveAsync_WrongAccount_DoesNotRemove()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        var recipient = await repo.AddAsync(new NotificationRecipient { AccountId = 1, Email = "a@x.com" });

        await repo.RemoveAsync(2, recipient.Id);

        (await repo.ListAsync(1)).Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveAsync_MatchingAccount_Removes()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        var recipient = await repo.AddAsync(new NotificationRecipient { AccountId = 1, Email = "a@x.com" });

        await repo.RemoveAsync(1, recipient.Id);

        (await repo.ListAsync(1)).Should().BeEmpty();
    }

    [Fact]
    public async Task SetTradeApprovalAsync_Enabled_AddsFlagToCategories()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        var recipient = await repo.AddAsync(new NotificationRecipient { AccountId = 1, Email = "a@x.com", Categories = NotificationCategory.DailyReport });

        var result = await repo.SetTradeApprovalAsync(1, recipient.Id, true);

        result.Should().BeTrue();
        var updated = (await repo.ListAsync(1)).Single();
        updated.Categories.Should().HaveFlag(NotificationCategory.TradeApproval);
        updated.Categories.Should().HaveFlag(NotificationCategory.DailyReport);
    }

    [Fact]
    public async Task SetTradeApprovalAsync_Disabled_RemovesFlagButKeepsOthers()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        var recipient = await repo.AddAsync(new NotificationRecipient
        {
            AccountId = 1,
            Email = "a@x.com",
            Categories = NotificationCategory.DailyReport | NotificationCategory.TradeApproval,
        });

        await repo.SetTradeApprovalAsync(1, recipient.Id, false);

        var updated = (await repo.ListAsync(1)).Single();
        updated.Categories.Should().NotHaveFlag(NotificationCategory.TradeApproval);
        updated.Categories.Should().HaveFlag(NotificationCategory.DailyReport);
    }

    [Fact]
    public async Task SetTradeApprovalAsync_UnknownRecipient_ReturnsFalse()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);

        var result = await repo.SetTradeApprovalAsync(1, 999, true);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateEmailIfMatchesAsync_MatchingEmail_Updates()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        await repo.AddAsync(new NotificationRecipient { AccountId = 1, Email = "old@x.com" });

        await repo.UpdateEmailIfMatchesAsync(1, "old@x.com", "new@x.com");

        (await repo.ListAsync(1)).Single().Email.Should().Be("new@x.com");
    }

    [Fact]
    public async Task UpdateEmailIfMatchesAsync_NonMatchingEmail_NoOp()
    {
        await using var db = CreateDb();
        var repo = new NotificationRecipientRepository(db);
        await repo.AddAsync(new NotificationRecipient { AccountId = 1, Email = "old@x.com" });

        await repo.UpdateEmailIfMatchesAsync(1, "different@x.com", "new@x.com");

        (await repo.ListAsync(1)).Single().Email.Should().Be("old@x.com");
    }
}
