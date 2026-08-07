using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class FilingEventRepository(SwingTraderDbContext db) : IFilingEventRepository
{
    public Task<bool> ExistsAsync(string accessionNumber, CancellationToken ct = default) =>
        db.FilingEvents.AnyAsync(e => e.AccessionNumber == accessionNumber, ct);

    public async Task AddAsync(FilingEvent evt, CancellationToken ct = default)
    {
        db.FilingEvents.Add(evt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Leave the context clean. A rejected entity stays tracked as
            // Added, so the NEXT SaveChanges - very often the caller's own
            // error logging - replays the same rejection and buries the
            // original error (7 Aug 2026: hours lost to exactly this).
            db.Entry(evt).State = EntityState.Detached;
            throw;
        }
    }

    public Task<List<FilingEvent>> GetRecentAsync(int days, CancellationToken ct = default)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));
        return db.FilingEvents.AsNoTracking()
            .Where(e => e.FiledAt >= since)
            .OrderByDescending(e => e.FiledAt).ThenByDescending(e => e.Id)
            .Take(500)
            .ToListAsync(ct);
    }
}
