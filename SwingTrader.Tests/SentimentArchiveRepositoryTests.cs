using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using Xunit;

namespace SwingTrader.Tests;

// The proprietary sentiment archive: same-day idempotency (two accounts
// researching one symbol must not double-insert), retention pruning that
// never touches scores, and stats. Plus the Tiingo news DTO pinned to a real
// captured payload.
public class SentimentArchiveRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SentimentDailyScore Score(string symbol, DateOnly date, decimal score = 0.5m) =>
        new() { Symbol = symbol, Date = date, Score = score, ArticleCount = 3, Model = "claude-sonnet-5" };

    private static SentimentArticle Article(string symbol, DateOnly date, string url) =>
        new() { Symbol = symbol, Date = date, Source = "Tiingo", Title = "T", Url = url, PublishedAtUtc = DateTime.UtcNow };

    [Fact]
    public async Task SaveDailyScore_SecondSameDayWrite_IsANoOp()
    {
        await using var db = CreateDb();
        var repo = new SentimentArchiveRepository(db);
        var today = new DateOnly(2026, 7, 10);

        await repo.SaveDailyScoreAsync(Score("AAPL", today, 0.4m));
        await repo.SaveDailyScoreAsync(Score("AAPL", today, -0.9m)); // second account, same day

        var rows = await db.SentimentDailyScores.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].Score.Should().Be(0.4m); // first write wins, no exception
    }

    [Fact]
    public async Task SaveArticles_AlreadyArchivedUrls_Skipped()
    {
        await using var db = CreateDb();
        var repo = new SentimentArchiveRepository(db);
        var today = new DateOnly(2026, 7, 10);

        await repo.SaveArticlesAsync([Article("AAPL", today, "https://a.com/1")]);
        await repo.SaveArticlesAsync(
        [
            Article("AAPL", today, "https://a.com/1"),  // dupe
            Article("AAPL", today, "https://a.com/2"),  // new
        ]);

        (await db.SentimentArticles.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task PruneArticles_DeletesOnlyAgedArticles_NeverScores()
    {
        await using var db = CreateDb();
        var repo = new SentimentArchiveRepository(db);
        var old = new DateOnly(2024, 6, 1);
        var recent = new DateOnly(2026, 7, 1);

        await repo.SaveArticlesAsync([Article("AAPL", old, "https://a.com/old")]);
        await repo.SaveArticlesAsync([Article("AAPL", recent, "https://a.com/new")]);
        await repo.SaveDailyScoreAsync(Score("AAPL", old)); // scores are forever

        var pruned = await repo.PruneArticlesAsync(olderThan: new DateOnly(2024, 7, 10));

        pruned.Should().Be(1);
        (await db.SentimentArticles.SingleAsync()).Url.Should().Be("https://a.com/new");
        (await db.SentimentDailyScores.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetStats_CountsAndOldestScoreDate()
    {
        await using var db = CreateDb();
        var repo = new SentimentArchiveRepository(db);

        (await repo.GetStatsAsync()).Should().Be(new SentimentArchiveStats(0, 0, null));

        await repo.SaveDailyScoreAsync(Score("AAPL", new DateOnly(2026, 7, 9)));
        await repo.SaveDailyScoreAsync(Score("MSFT", new DateOnly(2026, 7, 10)));
        await repo.SaveArticlesAsync([Article("AAPL", new DateOnly(2026, 7, 9), "https://a.com/1")]);

        (await repo.GetStatsAsync()).Should().Be(
            new SentimentArchiveStats(2, 1, new DateOnly(2026, 7, 9)));
    }

    [Fact]
    public void TiingoNewsItem_DeserializesRealCapturedPayload()
    {
        // Verbatim shape from a real Power-plan response, 2026-07-10.
        const string payload = """
            [{"id":97126804,"publishedDate":"2026-07-10T21:30:30.430492Z",
              "title":"Apple (AAPL) Files Lawsuit Against OpenAI for Trade Secret Misappropriation",
              "url":"https://www.gurufocus.com/news/8954031/x","description":null,
              "source":"gurufocus.com","tags":["Stock","Technology"],
              "crawlDate":"2026-07-10T21:30:30.780968Z","tickers":["aapl","aapl"]}]
            """;

        var items = JsonSerializer.Deserialize<List<TiingoNewsItem>>(payload)!;

        items.Should().HaveCount(1);
        items[0].Id.Should().Be(97126804);
        items[0].Description.Should().BeNull();
        items[0].Source.Should().Be("gurufocus.com");
        items[0].PublishedDate.Year.Should().Be(2026);
    }
}
