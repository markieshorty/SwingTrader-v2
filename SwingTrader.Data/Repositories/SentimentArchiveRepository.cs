using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class SentimentArchiveRepository(SwingTraderDbContext db) : ISentimentArchiveRepository
{
    public async Task SaveDailyScoreAsync(SentimentDailyScore score, CancellationToken ct = default)
    {
        var exists = await db.SentimentDailyScores
            .AnyAsync(s => s.Symbol == score.Symbol && s.Date == score.Date, ct);
        if (exists) return;

        db.SentimentDailyScores.Add(score);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two accounts researching the same symbol raced past the
            // existence check; the unique (Symbol, Date) index arbitrates and
            // the loser's row is simply already archived.
            db.Entry(score).State = EntityState.Detached;
        }
    }

    public async Task SaveArticlesAsync(IReadOnlyList<SentimentArticle> articles, CancellationToken ct = default)
    {
        if (articles.Count == 0) return;

        var symbol = articles[0].Symbol;
        var date = articles[0].Date;
        var existingUrls = (await db.SentimentArticles
                .Where(a => a.Symbol == symbol && a.Date == date)
                .Select(a => a.Url)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fresh = articles
            .Where(a => a.Url is null || !existingUrls.Contains(a.Url))
            .ToList();
        if (fresh.Count == 0) return;

        db.SentimentArticles.AddRange(fresh);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var a in fresh) db.Entry(a).State = EntityState.Detached;
        }
    }

    public Task<List<SentimentDailyScore>> GetRecentScoresAsync(
        string symbol, DateOnly before, int lookbackDays, CancellationToken ct = default)
    {
        var oldest = before.AddDays(-lookbackDays);
        return db.SentimentDailyScores
            .Where(s => s.Symbol == symbol && s.Date < before && s.Date >= oldest)
            .OrderBy(s => s.Date)
            .ToListAsync(ct);
    }

    public async Task<int> PruneArticlesAsync(DateOnly olderThan, CancellationToken ct = default)
    {
        // Load-and-remove rather than ExecuteDelete: the weekly prune only
        // ages out ~a week of rows at a time (small), and this stays
        // exercisable against the InMemory test provider.
        var aged = await db.SentimentArticles.Where(a => a.Date < olderThan).ToListAsync(ct);
        if (aged.Count == 0) return 0;
        db.SentimentArticles.RemoveRange(aged);
        await db.SaveChangesAsync(ct);
        return aged.Count;
    }

    public async Task<SentimentArchiveStats> GetStatsAsync(CancellationToken ct = default)
    {
        var scoreCount = await db.SentimentDailyScores.CountAsync(ct);
        var articleCount = await db.SentimentArticles.CountAsync(ct);
        DateOnly? oldest = scoreCount > 0
            ? await db.SentimentDailyScores.MinAsync(s => s.Date, ct)
            : null;
        return new SentimentArchiveStats(scoreCount, articleCount, oldest);
    }
}
