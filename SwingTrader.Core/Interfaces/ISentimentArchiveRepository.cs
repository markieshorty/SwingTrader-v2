using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

// Writes are best-effort from the research pipeline's point of view: a
// second account researching the same symbol the same day hits the unique
// (Symbol, Date) index and must be swallowed as "already archived", never
// bubbled up to fail the research run.
public interface ISentimentArchiveRepository
{
    /// <summary>Inserts the day's score for a symbol; no-op if one already exists.</summary>
    Task SaveDailyScoreAsync(SentimentDailyScore score, CancellationToken ct = default);

    /// <summary>Inserts article metadata; already-archived (Symbol, Date, Url) rows are skipped.</summary>
    Task SaveArticlesAsync(IReadOnlyList<SentimentArticle> articles, CancellationToken ct = default);

    /// <summary>Deletes article metadata older than the cutoff. Scores are never pruned.</summary>
    Task<int> PruneArticlesAsync(DateOnly olderThan, CancellationToken ct = default);

    /// <summary>
    /// Daily scores for a symbol strictly BEFORE <paramref name="before"/> and no older
    /// than <paramref name="lookbackDays"/> before it - the sentiment-momentum baseline
    /// (today's score is deliberately excluded so the delta compares against history).
    /// </summary>
    Task<List<SentimentDailyScore>> GetRecentScoresAsync(string symbol, DateOnly before, int lookbackDays, CancellationToken ct = default);

    Task<SentimentArchiveStats> GetStatsAsync(CancellationToken ct = default);
}
