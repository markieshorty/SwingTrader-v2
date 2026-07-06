using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class AdminRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppUser MakeUser(string userId, int accountId, AccountRole role = AccountRole.Owner, bool isOnboarded = true) => new()
    {
        UserId = userId,
        Email = $"{userId}@example.com",
        DisplayName = userId,
        AccountId = accountId,
        Role = role,
        IsOnboarded = isOnboarded,
        FirstLoginAt = DateTime.UtcNow,
        LastLoginAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetStatsAsync_CountsUsersAndModesCorrectly()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        db.Accounts.Add(new Account { Id = 2, TradingMode = TradingMode.Live });
        db.AppUsers.Add(MakeUser("u1", 1));
        db.AppUsers.Add(MakeUser("u2", 2, isOnboarded: false));
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var stats = await repo.GetStatsAsync();

        stats.TotalUsers.Should().Be(2);
        stats.UsersNotOnboarded.Should().Be(1);
        stats.UsersInDemoMode.Should().Be(1);
        stats.UsersInLiveMode.Should().Be(1);
    }

    [Fact]
    public async Task GetStatsAsync_ExcludesDeletedAccountsFromModeCounts()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Live, IsDeleted = true });
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var stats = await repo.GetStatsAsync();

        stats.UsersInLiveMode.Should().Be(0);
    }

    [Fact]
    public async Task GetStatsAsync_ExcludesUsersWithDeletedAccountFromTotalUsers()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        db.Accounts.Add(new Account { Id = 2, TradingMode = TradingMode.Live, IsDeleted = true });
        db.AppUsers.Add(MakeUser("u1", 1));
        db.AppUsers.Add(MakeUser("u2", 2));
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var stats = await repo.GetStatsAsync();

        stats.TotalUsers.Should().Be(1);
    }

    [Fact]
    public async Task GetUsersAsync_FlagsButDoesNotExcludeUsersWithDeletedAccount()
    {
        // Deliberately not excluded, unlike GetStatsAsync's counts - a
        // leftover row from before the delete-cleanup fix existed needs to
        // stay visible so admin can click Delete on it to actually clean it
        // up, rather than it being invisibly stuck forever.
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        db.Accounts.Add(new Account { Id = 2, TradingMode = TradingMode.Live, IsDeleted = true });
        db.AppUsers.Add(MakeUser("u1", 1));
        db.AppUsers.Add(MakeUser("u2", 2));
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var users = await repo.GetUsersAsync();

        users.Should().Contain(u => u.UserId == "u1" && !u.AccountDeleted);
        users.Should().Contain(u => u.UserId == "u2" && u.AccountDeleted);
    }

    [Fact]
    public async Task GetStatsAsync_ComputesAverageWinRateAcrossAllClosedTrades()
    {
        await using var db = CreateDb();
        db.Trades.AddRange(
            new Trade { AccountId = 1, Symbol = "A", Status = TradeStatus.Closed, RealizedPnl = 10m, EntryPrice = 1, Quantity = 1 },
            new Trade { AccountId = 1, Symbol = "B", Status = TradeStatus.Closed, RealizedPnl = -5m, EntryPrice = 1, Quantity = 1 },
            new Trade { AccountId = 1, Symbol = "C", Status = TradeStatus.Open, RealizedPnl = null, EntryPrice = 1, Quantity = 1 });
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var stats = await repo.GetStatsAsync();

        stats.TotalTradesAllTime.Should().Be(2); // open trade excluded
        stats.AverageWinRateAllUsers.Should().Be(0.5m);
    }

    [Fact]
    public async Task GetUserAsync_BuildsSummaryFromAccountAndRiskProfile()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        db.AppUsers.Add(MakeUser("u1", 1));
        db.AccountRiskProfiles.Add(new AccountRiskProfile { AccountId = 1, LockedCapitalPct = 0.90m });
        db.Watchlists.Add(new Watchlist { AccountId = 1, Name = "AI Picks", IsEnabled = true, IsDefault = true });
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var summary = await repo.GetUserAsync("u1");

        summary.Should().NotBeNull();
        summary!.RiskLabel.Should().Be("Very Conservative");
        summary.EnabledWatchlistCount.Should().Be(1);
        summary.TradingMode.Should().Be(TradingMode.Demo);
    }

    [Fact]
    public async Task GetUserAsync_UnknownUser_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new AdminRepository(db);

        var summary = await repo.GetUserAsync("nobody");

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetJobFailuresAsync_OnlyReturnsFailedWithinLookback()
    {
        await using var db = CreateDb();
        db.JobLogEntries.AddRange(
            new JobLogEntry { AccountId = 1, JobType = "Research", JobDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = JobStatus.Failed, EnqueuedAt = DateTime.UtcNow.AddHours(-1) },
            new JobLogEntry { AccountId = 1, JobType = "Execution", JobDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = JobStatus.Completed, EnqueuedAt = DateTime.UtcNow.AddHours(-1) },
            new JobLogEntry { AccountId = 1, JobType = "Monitor", JobDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = JobStatus.Failed, EnqueuedAt = DateTime.UtcNow.AddDays(-3) });
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var failures = await repo.GetJobFailuresAsync(TimeSpan.FromHours(48));

        failures.Should().ContainSingle(f => f.JobType == "Research");
    }

    [Fact]
    public async Task GetJobFailuresAsync_IncludesOwnerEmail()
    {
        await using var db = CreateDb();
        db.AppUsers.Add(MakeUser("owner1", 1, AccountRole.Owner));
        db.JobLogEntries.Add(new JobLogEntry
        {
            AccountId = 1, JobType = "Research", JobDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = JobStatus.Failed, EnqueuedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var failures = await repo.GetJobFailuresAsync(TimeSpan.FromHours(48));

        failures.Single().OwnerEmail.Should().Be("owner1@example.com");
    }

    [Fact]
    public async Task RetryJobAsync_FailedJob_RemovesEntryAndReturnsTrue()
    {
        await using var db = CreateDb();
        var entry = new JobLogEntry { AccountId = 1, JobType = "Research", JobDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = JobStatus.Failed, EnqueuedAt = DateTime.UtcNow };
        db.JobLogEntries.Add(entry);
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var result = await repo.RetryJobAsync(entry.Id);

        result.Should().BeTrue();
        (await db.JobLogEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RetryJobAsync_NonFailedJob_ReturnsFalseAndLeavesEntry()
    {
        await using var db = CreateDb();
        var entry = new JobLogEntry { AccountId = 1, JobType = "Research", JobDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = JobStatus.Completed, EnqueuedAt = DateTime.UtcNow };
        db.JobLogEntries.Add(entry);
        await db.SaveChangesAsync();
        var repo = new AdminRepository(db);

        var result = await repo.RetryJobAsync(entry.Id);

        result.Should().BeFalse();
        (await db.JobLogEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RetryJobAsync_UnknownId_ReturnsFalse()
    {
        await using var db = CreateDb();
        var repo = new AdminRepository(db);

        var result = await repo.RetryJobAsync(999);

        result.Should().BeFalse();
    }
}
