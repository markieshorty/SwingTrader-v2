using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class SymbolLifecycleRepository(SwingTraderDbContext db) : ISymbolLifecycleRepository
{
    public async Task AddAsync(SymbolLifecycle lifecycle, CancellationToken ct = default)
    {
        // Upsert on the unique Symbol index - a resumed backfill re-adding a
        // symbol's lifecycle must not throw.
        var existing = await db.SymbolLifecycles.FirstOrDefaultAsync(x => x.Symbol == lifecycle.Symbol, ct);
        if (existing is null)
            db.SymbolLifecycles.Add(lifecycle);
        else
        {
            existing.ListedAt = lifecycle.ListedAt;
            existing.DelistedAt = lifecycle.DelistedAt;
            existing.EndReason = lifecycle.EndReason;
        }
        await db.SaveChangesAsync(ct);
    }

    public Task<Dictionary<string, SymbolLifecycle>> GetAllAsync(CancellationToken ct = default) =>
        db.SymbolLifecycles.AsNoTracking()
            .ToDictionaryAsync(x => x.Symbol, StringComparer.OrdinalIgnoreCase, ct);

    public Task<int> CountAsync(CancellationToken ct = default) =>
        db.SymbolLifecycles.CountAsync(ct);
}
