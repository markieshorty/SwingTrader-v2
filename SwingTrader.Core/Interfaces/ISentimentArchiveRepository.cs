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

    Task<SentimentArchiveStats> GetStatsAsync(CancellationToken ct = default);
}
