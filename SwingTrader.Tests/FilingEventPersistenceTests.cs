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
        SwingTraderDbContext db, IReadOnlyList<EdgarEightK> filings, decimal? publicFloat = 50_000_000m)
    {
        var edgar = Substitute.For<IEdgarClient>();
        edgar.SearchEightKsAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(filings);
        edgar.GetPublicFloatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(publicFloat);

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

    private static EdgarEightK Filing(string ticker, string accession, string sic = "3060") =>
        new("0001234567", ticker, "Test Corp", accession, "doc.htm",
            new DateOnly(2026, 8, 6), ["4.02"], sic);

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

    // 7 Aug 2026: the "not in our liquid universe" proxy let Yum China
    // ($16.5bn float) and iRhythm ($4.9bn) into a feed whose entire premise is
    // companies nobody researches. Size is now decided by the SEC's own
    // public-float figure instead.
    [Theory]
    [InlineData(16_500_000_000, false)]   // Yum China - was polluting the feed
    [InlineData(4_900_000_000, false)]    // iRhythm
    [InlineData(382_600_000, false)]      // ARKO - mid, still excluded
    [InlineData(63_478_476, true)]        // authID - genuinely small
    [InlineData(1_655_027, true)]         // Vystar - micro
    public async Task OnlySmallFloatCompanies_AreCaptured(decimal publicFloat, bool expectCaptured)
    {
        await using var db = CreateDb();
        var service = CreateService(db, [Filing("TEST", "acc-1")], publicFloat);

        await service.ScanAsync(new DateOnly(2026, 8, 6));

        (await db.FilingEvents.CountAsync()).Should().Be(expectCaptured ? 1 : 0);
    }

    [Fact]
    public async Task CapturedEvents_RecordTheFloatTheyWereJudgedOn()
    {
        // H-FE2 is declared on sub-$500M names, so the figure the gate used
        // has to be stored or the hypothesis stays untestable.
        await using var db = CreateDb();
        var service = CreateService(db, [Filing("TEST", "acc-1")], 63_478_476m);

        await service.ScanAsync(new DateOnly(2026, 8, 6));

        (await db.FilingEvents.SingleAsync()).MarketCapUsd.Should().Be(63_478_476m);
    }

    [Fact]
    public async Task UnknownFloat_IsExcludedRatherThanGuessed()
    {
        await using var db = CreateDb();
        var service = CreateService(db, [Filing("TEST", "acc-1")], publicFloat: null);

        await service.ScanAsync(new DateOnly(2026, 8, 6));

        (await db.FilingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BlankChequeShells_AreExcludedBeforeAnyFloatLookup()
    {
        // SPACs clear a float test easily but file deal mechanics, not
        // fundamentals. SIC arrives free in the search hit, so the exclusion
        // must cost no request at all.
        await using var db = CreateDb();
        var edgar = Substitute.For<IEdgarClient>();
        edgar.SearchEightKsAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([Filing("COLA", "acc-1", sic: "6770")]);
        var universe = Substitute.For<IMarketUniverseService>();
        universe.GetUniverseAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());

        var service = new FilingEventScanService(
            edgar, new FilingEventRepository(db), universe,
            Substitute.For<IUserHttpClientFactory>(), Substitute.For<IClaudeRateLimiter>(),
            Options.Create(new FilingEventsConfig { Enabled = true, MaxClassificationsPerDay = 40 }),
            Options.Create(new ClaudeConfig()),
            Substitute.For<ILogger<FilingEventScanService>>());

        await service.ScanAsync(new DateOnly(2026, 8, 6));

        (await db.FilingEvents.CountAsync()).Should().Be(0);
        await edgar.DidNotReceive().GetPublicFloatAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("6770", true)]     // blank cheques
    [InlineData("3060", false)]    // Vystar's actual industry
    [InlineData("", false)]        // EDGAR gave us nothing - not grounds to drop
    [InlineData(null, false)]
    public void IsExcludedIndustry_OnlyDropsListedCodes(string? sic, bool excluded) =>
        FilingEventScanService.IsExcludedIndustry(sic, null).Should().Be(excluded);

    [Fact]
    public async Task FloatIsLookedUpOncePerCompany_NotOncePerFiling()
    {
        // Float restates annually; a filer with several 8-Ks in one day must
        // not cost several requests.
        await using var db = CreateDb();
        var service = CreateService(db, [
            Filing("TEST", "acc-1"), Filing("TEST", "acc-2"), Filing("TEST", "acc-3"),
        ], 50_000_000m);

        await service.ScanAsync(new DateOnly(2026, 8, 6));

        (await db.FilingEvents.CountAsync()).Should().Be(3);
    }
}
