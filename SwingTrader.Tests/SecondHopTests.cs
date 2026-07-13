using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Agents.SecondHop;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// Second-hop news pipeline (docs/second-hop-plan SH1): the combine/decay
// math, the defensive parse rules for both Claude responses (graph build +
// relevance pass), and the repository behaviours the shadow relies on -
// notably that a human suppression outlives a graph rebuild.
public class SecondHopTests
{
    // ── Combine / decay ───────────────────────────────────────────────────────

    [Fact]
    public void Combine_DecaysByAgeAndClampsThePileOn()
    {
        var asOf = new DateOnly(2026, 7, 13);

        // A fresh event counts in full; a 7-calendar-day-old one (5 trading
        // days = one half-life) counts half.
        var mixed = SecondHopMath.Combine(
        [
            new SecondHopMath.TransmittedEvent(0.4m, asOf),
            new SecondHopMath.TransmittedEvent(0.4m, asOf.AddDays(-7)),
        ], asOf, halfLifeTradingDays: 5);
        ((double)mixed).Should().BeApproximately(0.6, 0.01);

        // Five strong fresh events would sum to 3.0 - the clamp keeps the
        // score inside the honest [-1, 1] band.
        var pileOn = SecondHopMath.Combine(
            Enumerable.Repeat(new SecondHopMath.TransmittedEvent(0.6m, asOf), 5).ToList(),
            asOf, halfLifeTradingDays: 5);
        pileOn.Should().Be(1m);

        SecondHopMath.Combine([], asOf, 5).Should().Be(0m);
    }

    // ── Graph-build parsing (EconomicLinkService.ParseLinks) ─────────────────

    [Fact]
    public void ParseLinks_EnforcesTheDefensiveRules()
    {
        var raw = """
            {"links": [
              {"name": "TSMC", "ticker": "tsm", "relation": "Supplier", "transmission": "capacity up = bullish", "strength": 1.7, "rationale": "Sole leading-edge foundry for its GPUs"},
              {"name": "Itself", "ticker": "NVDA", "relation": "Competitor", "transmission": "n/a", "strength": 0.5, "rationale": "self"},
              {"name": "Mystery Corp", "ticker": "MYS", "relation": "Frenemy", "transmission": "?", "strength": 0.5, "rationale": "unknown relation type"},
              {"name": "No Reason Inc", "ticker": "NRI", "relation": "Customer", "transmission": "x", "strength": 0.5, "rationale": ""},
              {"name": "Private Fab", "ticker": null, "relation": "Supplier", "transmission": "supply risk", "strength": 0.3, "rationale": "Key private component maker"}
            ]}
            """;

        var links = EconomicLinkService.ParseLinks(raw, "NVDA", "claude-test");

        links.Should().HaveCount(2); // self-link, bad relation and empty rationale all dropped
        links[0].LinkedTicker.Should().Be("TSM");     // uppercased
        links[0].Strength.Should().Be(1m);            // clamped
        links[1].LinkedTicker.Should().BeNull();      // private company kept for context
    }

    // ── Relevance parsing (SecondHopScorer) ──────────────────────────────────

    [Fact]
    public void ParseTransmissions_ClampsImpact_AndSurvivesCodeFences()
    {
        var raw = "```json\n{\"events\": [" +
                  "{\"index\": 0, \"transmits\": true, \"impact\": 2.0}," +
                  "{\"index\": 1, \"transmits\": false, \"impact\": 0.0}]," +
                  "\"summary\": \"TSMC guidance transmits via supply.\"}\n```";

        var events = SecondHopScorer.ParseTransmissions(raw);

        events.Should().HaveCount(2);
        events[0].Impact.Should().Be(1m); // clamped
        events[1].Transmits.Should().BeFalse();
        SecondHopScorer.ParseSummary(raw).Should().Be("TSMC guidance transmits via supply.");
    }

    // ── Repository ────────────────────────────────────────────────────────────

    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EconomicLink Link(string name, string? ticker = null, bool suppressed = false) => new()
    {
        LinkedName = name,
        LinkedTicker = ticker,
        Relation = "Supplier",
        TransmissionNote = "t",
        Strength = 0.5m,
        Rationale = "r",
        Suppressed = suppressed,
    };

    [Fact]
    public async Task ReplaceLinks_PreservesHumanSuppressionAcrossRebuilds()
    {
        // The kill switch must outlive a refresh: if Claude keeps producing a
        // hallucinated link, the human veto keeps killing it.
        await using var db = CreateDb();
        var repo = new EconomicLinkRepository(db);

        await repo.ReplaceLinksAsync("NVDA", [Link("Hallucinated Corp", "HAL1"), Link("Real Supplier", "TSM")]);
        var first = await repo.GetLinksAsync("NVDA");
        (await repo.SetSuppressedAsync(first.First(l => l.LinkedName == "Hallucinated Corp").Id, true)).Should().BeTrue();

        // Rebuild produces the same two links again.
        await repo.ReplaceLinksAsync("NVDA", [Link("Hallucinated Corp", "HAL1"), Link("Real Supplier", "TSM")]);

        var rebuilt = await repo.GetLinksAsync("NVDA");
        rebuilt.Should().HaveCount(2);
        rebuilt.First(l => l.LinkedName == "Hallucinated Corp").Suppressed.Should().BeTrue();
        rebuilt.First(l => l.LinkedName == "Real Supplier").Suppressed.Should().BeFalse();
    }

    [Fact]
    public async Task GetStaleSymbols_FlagsMissingAndAgedGraphs()
    {
        await using var db = CreateDb();
        var repo = new EconomicLinkRepository(db);

        await repo.ReplaceLinksAsync("AAPL", [Link("Fresh Link", "FSH")]);
        db.EconomicLinks.Add(new EconomicLink
        {
            Symbol = "MSFT", LinkedName = "Old Link", Relation = "Customer",
            TransmissionNote = "t", Rationale = "r", BuiltAt = DateTime.UtcNow.AddDays(-45),
        });
        await db.SaveChangesAsync();

        var stale = await repo.GetStaleSymbolsAsync(["AAPL", "MSFT", "NVDA"], TimeSpan.FromDays(30));

        stale.Should().BeEquivalentTo(["MSFT", "NVDA"]); // aged + never built; AAPL fresh
    }

    [Fact]
    public async Task GetScoresForSymbolsSince_ReadsAcrossSymbolsInclusive()
    {
        await using var db = CreateDb();
        var repo = new SentimentArchiveRepository(db);
        var day = new DateOnly(2026, 7, 10);

        await repo.SaveDailyScoreAsync(new SentimentDailyScore { Symbol = "TSM", Date = day, Score = 0.6m });
        await repo.SaveDailyScoreAsync(new SentimentDailyScore { Symbol = "ASML", Date = day.AddDays(-1), Score = -0.4m });
        await repo.SaveDailyScoreAsync(new SentimentDailyScore { Symbol = "ASML", Date = day.AddDays(-10), Score = 0.9m }); // too old
        await repo.SaveDailyScoreAsync(new SentimentDailyScore { Symbol = "XOM", Date = day, Score = 0.8m });               // not requested

        var scores = await new SentimentArchiveRepository(db)
            .GetScoresForSymbolsSinceAsync(["tsm", "ASML"], day.AddDays(-5));

        scores.Should().HaveCount(2);
        scores.Select(s => s.Symbol).Should().BeEquivalentTo(["TSM", "ASML"]);
    }

    // ── Bellwether score parsing ──────────────────────────────────────────────

    [Fact]
    public void BellwetherParseScore_ClampsAndStripsFences()
    {
        BellwetherSyncService.ParseScore("{\"sentiment_score\": -3.2}").Should().Be(-1m);
        BellwetherSyncService.ParseScore("```json\n{\"sentiment_score\": 0.45}\n```").Should().Be(0.45m);
    }
}
