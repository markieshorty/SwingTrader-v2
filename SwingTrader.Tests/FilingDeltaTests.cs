using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Agents.Filings;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.Configuration;
using Xunit;

namespace SwingTrader.Tests;

// Filing-delta pipeline (docs/filing-delta-plan FD1): the pure text machinery
// (extraction, the hash gate, the paragraph diff), the decay math, the Claude
// response parsing rules, and the repository reads the shadow relies on.
public class FilingDeltaTests
{
    // ── HTML -> text + section extraction ────────────────────────────────────

    private static string TenKHtml(string riskBody, string mdaBody) =>
        "<html><body>" +
        "<p>Item 1A. Risk Factors</p><p>(see full section below)</p>" + // ToC-style early mention
        "<h2>Item 1.</h2><p>Business description here.</p>" +
        $"<h2>Item 1A.</h2>{riskBody}" +
        "<h2>Item 1B.</h2><p>Unresolved staff comments.</p>" +
        $"<h2>Item 7.</h2>{mdaBody}" +
        "<h2>Item 7A.</h2><p>Quantitative disclosures.</p>" +
        "</body></html>";

    private static string Paragraphs(string marker, int count) =>
        string.Concat(Enumerable.Range(0, count).Select(i =>
            $"<p>{marker} paragraph {i}: this body text is deliberately long enough to clear the eighty " +
            "character diffable-prose floor used by the paragraph indexer in the extractor.</p>"));

    [Fact]
    public void ExtractSections_TenK_FindsRiskFactorsAndMda_IgnoringTocMentions()
    {
        var text = FilingTextExtractor.HtmlToText(TenKHtml(Paragraphs("RISK", 10), Paragraphs("MDA", 10)));

        var sections = FilingTextExtractor.ExtractSections(text, "10-K");

        sections.RiskFactors.Should().NotBeNull();
        sections.RiskFactors.Should().Contain("RISK paragraph 9").And.NotContain("MDA paragraph");
        sections.Mda.Should().NotBeNull();
        sections.Mda.Should().Contain("MDA paragraph 0").And.NotContain("Quantitative disclosures");
    }

    [Fact]
    public void ExtractSections_TenQ_MdaPicksPartI_NotPartIIUnregisteredSales()
    {
        // A 10-Q has TWO "Item 2" headers: Part I Item 2 is the (long) MD&A,
        // Part II Item 2 is the short Unregistered Sales item. Last-occurrence
        // extraction picked Part II; longest-section-wins must pick the MD&A.
        var html =
            "<h2>Item 1.</h2><p>Financial statements.</p>" +
            $"<h2>Item 2.</h2>{Paragraphs("MDA", 12)}" +                    // Part I MD&A (long)
            "<h2>Item 3.</h2><p>Quantitative disclosures about market risk.</p>" +
            "<h2>Item 4.</h2><p>Controls and procedures.</p>" +
            $"<h2>Item 1A.</h2>{Paragraphs("RISK", 8)}" +                   // Part II Risk Factors
            $"<h2>Item 2.</h2>{Paragraphs("SALES", 4)}" +                   // Part II Unregistered Sales (short)
            "<h2>Item 5.</h2><p>Other information.</p>";
        var text = FilingTextExtractor.HtmlToText($"<html><body>{html}</body></html>");

        var sections = FilingTextExtractor.ExtractSections(text, "10-Q");

        sections.Mda.Should().NotBeNull();
        sections.Mda.Should().Contain("MDA paragraph 0").And.NotContain("SALES paragraph");
        sections.RiskFactors.Should().NotBeNull();
        sections.RiskFactors.Should().Contain("RISK paragraph 0").And.NotContain("SALES paragraph 1");
    }

    [Fact]
    public void ExtractSections_SectionTooShortToBeReal_IsNull()
    {
        // A ToC hit produces a tiny "section" - must be treated as extraction
        // failure, not hashed (it would flag every quarter as changed).
        var text = FilingTextExtractor.HtmlToText(
            "<p>Item 1A. Risk Factors ... 12</p><p>Item 1B. Comments ... 14</p>");

        FilingTextExtractor.ExtractSections(text, "10-K").RiskFactors.Should().BeNull();
    }

    // ── The hash gate ─────────────────────────────────────────────────────────

    [Fact]
    public void Hash_IsInsensitiveToCaseWhitespaceAndNumbers()
    {
        // Page numbers and dollar figures change every quarter even in
        // copy-paste text; the gate must only fire on LANGUAGE change.
        var a = FilingTextExtractor.Hash("Our revenue was $1,234 million\n on page 41.");
        var b = FilingTextExtractor.Hash("OUR REVENUE WAS  $5,678 MILLION on page 99.");
        var c = FilingTextExtractor.Hash("Our revenue declined materially on page 41.");

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    // ── Paragraph diff ────────────────────────────────────────────────────────

    [Fact]
    public void DiffParagraphs_FindsAddedAndRemoved_IgnoringReshuffles()
    {
        string P(string s) => s + " — padded to comfortably clear the eighty character diffable prose floor used by the indexer.";
        var oldText = $"{P("Alpha stays")}\n\n{P("Beta gets removed")}\n\n{P("Gamma stays")}";
        var newText = $"{P("Gamma stays")}\n\n{P("Alpha stays")}\n\n{P("Delta is new")}"; // reshuffled + 1 in, 1 out

        var diff = FilingTextExtractor.DiffParagraphs(oldText, newText);

        diff.Added.Should().ContainSingle(p => p.Contains("Delta is new"));
        diff.Removed.Should().ContainSingle(p => p.Contains("Beta gets removed"));
    }

    // ── Decay ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, -0.60)]     // filing day: full delta
    [InlineData(88, -0.30)]    // ~63 trading days later (88 calendar * 5/7): half
    [InlineData(176, -0.15)]   // two half-lives: quarter
    public void EffectiveScore_HalvesEveryHalfLife(int calendarDaysLater, double expected)
    {
        var filed = new DateOnly(2026, 1, 1);
        var asOf = filed.AddDays(calendarDaysLater);

        var effective = FilingDeltaMath.EffectiveScore(-0.60m, filed, asOf, halfLifeTradingDays: 63);

        ((double)effective).Should().BeApproximately(expected, 0.01);
    }

    // ── Claude response parsing ───────────────────────────────────────────────

    [Fact]
    public void ParseDeltaResponse_ClampsAndComputesDelta_AndStripsCodeFences()
    {
        var raw = "```json\n{\"direction\": -2.5, \"materiality\": 0.8, " +
                  "\"categories\": [\"litigation\", \"liquidity\"], \"summary\": \"New going-concern language.\"}\n```";

        var delta = FilingSyncService.ParseDeltaResponse(raw, "claude-test");

        delta.Direction.Should().Be(-1m); // clamped
        delta.Materiality.Should().Be(0.8m);
        delta.Delta.Should().Be(-0.8m);
        delta.Categories.Should().Be("litigation,liquidity");
        delta.Summary.Should().Be("New going-concern language.");
    }

    // ── Age-gate + model tiering (cost control) ───────────────────────────────

    [Theory]
    [InlineData(0, FilingScoreTier.Live)]        // filed today: fresh -> Sonnet
    [InlineData(30, FilingScoreTier.Live)]       // exactly the fresh cap: still fresh
    [InlineData(31, FilingScoreTier.Backfill)]   // just past fresh: backfill -> Haiku
    [InlineData(120, FilingScoreTier.Backfill)]  // exactly the max cap: still scored
    [InlineData(121, FilingScoreTier.SkipStale)] // past the cap: stored but not scored
    [InlineData(400, FilingScoreTier.SkipStale)] // ancient backfill: never scored
    public void PickScoreTier_TiersByFilingAge(int ageDays, FilingScoreTier expected)
    {
        var cfg = new FilingDeltaConfig { FreshScoringDays = 30, MaxScoringAgeDays = 120 };
        var filed = new DateOnly(2026, 1, 1);
        var asOf = filed.AddDays(ageDays);

        FilingSyncService.PickScoreTier(filed, asOf, cfg).Should().Be(expected);
    }

    // ── Repository ────────────────────────────────────────────────────────────

    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task GetLatestNonZeroDeltaAsync_SkipsUnchangedRows_PicksNewestByFiledDate()
    {
        await using var db = CreateDb();
        var repo = new FilingRepository(db);

        await repo.AddDeltaAsync(new FilingDelta { FilingId = 1, Symbol = "aapl", FiledAt = new DateOnly(2026, 1, 10), Delta = -0.5m });
        await repo.AddDeltaAsync(new FilingDelta { FilingId = 2, Symbol = "AAPL", FiledAt = new DateOnly(2026, 4, 10), Delta = 0m });     // unchanged quarter
        await repo.AddDeltaAsync(new FilingDelta { FilingId = 3, Symbol = "AAPL", FiledAt = new DateOnly(2026, 2, 10), Delta = 0.3m });
        await repo.AddDeltaAsync(new FilingDelta { FilingId = 4, Symbol = "MSFT", FiledAt = new DateOnly(2026, 6, 10), Delta = -0.9m });  // other symbol

        var latest = await repo.GetLatestNonZeroDeltaAsync("AAPL");

        latest.Should().NotBeNull();
        latest!.Delta.Should().Be(0.3m); // newest NON-ZERO for the symbol
    }

    [Fact]
    public async Task GetLatestAsync_ScopesBySymbolAndType()
    {
        await using var db = CreateDb();
        var repo = new FilingRepository(db);

        await repo.AddAsync(new Filing { Symbol = "AAPL", Cik = "0000320193", AccessionNumber = "a-1", FilingType = "10-Q", FiledAt = new DateOnly(2026, 1, 1), PrimaryDocument = "d.htm" });
        await repo.AddAsync(new Filing { Symbol = "AAPL", Cik = "0000320193", AccessionNumber = "a-2", FilingType = "10-Q", FiledAt = new DateOnly(2026, 4, 1), PrimaryDocument = "d.htm" });
        await repo.AddAsync(new Filing { Symbol = "AAPL", Cik = "0000320193", AccessionNumber = "a-3", FilingType = "10-K", FiledAt = new DateOnly(2026, 6, 1), PrimaryDocument = "d.htm" });

        var latestQ = await repo.GetLatestAsync("AAPL", "10-Q");

        latestQ!.AccessionNumber.Should().Be("a-2"); // newest 10-Q, not the newer 10-K
    }
}
