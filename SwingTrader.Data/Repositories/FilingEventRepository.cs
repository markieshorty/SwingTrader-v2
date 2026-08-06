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
        await db.SaveChangesAsync(ct);
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
