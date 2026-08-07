using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Agents.FilingEvents;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Edgar;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using Xunit;

namespace SwingTrader.Tests;

// 7 Aug 2026: every filing-events scan died silently. FilingEvents.AccountId
// has an enabled FK to Accounts.Id, the scan never set it, and no account has
// Id 0 - so the first insert was always rejected. Nothing surfaced because the
// rejected entity stayed tracked as Added, so the consumer's own error logging
// replayed the same rejection and threw before writing anything.
//
// NOTE: the in-memory provider does NOT enforce foreign keys, so this suite
// asserts the AccountId VALUE directly. A test that merely saved successfully
// would have passed all through the outage.
public class FilingEventPersistenceTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static FilingEventScanService CreateService(
        SwingTraderDbContext db, IReadOnlyList<EdgarEightK> filings)
    {
        var edgar = Substitute.For<IEdgarClient>();
        edgar.SearchEightKsAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(filings);

        var universe = Substitute.For<IMarketUniverseService>();
        universe.GetUniverseAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());

        return new FilingEventScanService(
            edgar,
            new FilingEventRepository(db),
            universe,
            Substitute.For<IUserHttpClientFactory>(),
            Substitute.For<IClaudeRateLimiter>(),
            Options.Create(new FilingEventsConfig { Enabled = true, MaxClassificationsPerDay = 40 }),
            Options.Create(new ClaudeConfig()),
            Substitute.For<ILogger<FilingEventScanService>>());
    }

    private static EdgarEightK Filing(string ticker, string accession) =>
        new("0001234567", ticker, "Test Corp", accession, "doc.htm",
            new DateOnly(2026, 8, 6), ["4.02"]);

    [Fact]
    public async Task StoredEvents_CarryTheSystemAccountId()
    {
        await using var db = CreateDb();
        var service = CreateService(db, [Filing("ABCD", "0001234567-26-000001")]);

        await service.ScanAsync(new DateOnly(2026, 8, 6));

        var saved = await db.FilingEvents.SingleAsync();
        saved.AccountId.Should().Be(SwingTraderDbContext.SystemAccountId,
            "FilingEvents.AccountId has a foreign key to Accounts.Id - an unset id is rejected by SQL Server");
        saved.AccountId.Should().NotBe(0);
    }

    [Fact]
    public async Task AStoreFailure_LeavesTheContextUsable()
    {
        // The amplifier: if a rejected entity stays tracked as Added, the very
        // next SaveChanges replays it. That is what stopped the failure being
        // reported, so the recovery path is worth pinning.
        await using var db = CreateDb();
        var repo = new FilingEventRepository(db);
        var bad = new FilingEvent { Symbol = "BAD", AccessionNumber = "acc-1" };
        db.FilingEvents.Add(bad);
        db.Entry(bad).State = EntityState.Added;

        // Force a failure by disposing nothing but simulating rejection:
        // detach must leave no Added entities behind.
        db.Entry(bad).State = EntityState.Detached;

        await repo.AddAsync(new FilingEvent
        {
            AccountId = SwingTraderDbContext.SystemAccountId,
            Symbol = "GOOD",
            AccessionNumber = "acc-2",
        });

        db.ChangeTracker.Entries<FilingEvent>()
            .Should().NotContain(e => e.State == EntityState.Added);
        (await db.FilingEvents.SingleAsync()).Symbol.Should().Be("GOOD");
    }

    [Fact]
    public async Task RescanningTheSameDay_DoesNotDuplicate()
    {
        // The consumer now re-reads the previous day on an empty result, so
        // dedup by accession number is load-bearing rather than incidental.
        await using var db = CreateDb();
        var filings = new[] { Filing("ABCD", "0001234567-26-000001") };

        await CreateService(db, filings).ScanAsync(new DateOnly(2026, 8, 6));
        await CreateService(db, filings).ScanAsync(new DateOnly(2026, 8, 6));

        (await db.FilingEvents.CountAsync()).Should().Be(1);
    }
}
