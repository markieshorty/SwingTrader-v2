namespace SwingTrader.Core.Models;

// The proprietary sentiment archive (edge-plan Phase 4): the one dataset
// money cannot buy retroactively. Two tables because the lifetimes differ -
// article METADATA is bulky and pruned at ArchiveRetentionMonths; daily
// SCORES are tiny and kept forever. Both are account-AGNOSTIC (sentiment is
// a symbol-level fact, pre-weights): unique indexes make the second
// account's same-day research run a no-op instead of a duplicate.

public class SentimentArticle
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly Date { get; set; }               // research day (UTC)
    public required string Source { get; set; }      // "Finnhub" | "Tiingo"
    public required string Title { get; set; }
    public string? Url { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public string? Description { get; set; }         // truncated to 1000 chars at write
}

public class SentimentDailyScore
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly Date { get; set; }
    public decimal Score { get; set; }               // Claude blend, -1.0 .. 1.0
    public int ArticleCount { get; set; }            // articles in the scored prompt
    public string? Model { get; set; }               // Claude model id that scored it
}

public sealed record SentimentArchiveStats(
    int ScoreCount, int ArticleCount, DateOnly? OldestScoreDate);
