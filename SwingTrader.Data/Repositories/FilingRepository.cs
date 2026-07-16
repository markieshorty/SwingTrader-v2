using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class FilingRepository(SwingTraderDbContext db) : IFilingRepository
{
    public Task<Filing?> GetLatestAsync(string symbol, string filingType, CancellationToken ct = default) =>
        db.Filings
            .Where(f => f.Symbol == symbol.ToUpper() && f.FilingType == filingType)
            .OrderByDescending(f => f.FiledAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefaultAsync(ct);

    public Task<bool> ExistsAsync(string accessionNumber, CancellationToken ct = default) =>
        db.Filings.AnyAsync(f => f.AccessionNumber == accessionNumber, ct);

    public async Task<Filing> AddAsync(Filing filing, CancellationToken ct = default)
    {
        filing.Symbol = filing.Symbol.ToUpperInvariant();
        filing.CreatedAt = DateTime.UtcNow;
        db.Filings.Add(filing);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two overlapping syncs raced past ExistsAsync; the unique
            // accession index arbitrates - return the winner's row.
            db.Entry(filing).State = EntityState.Detached;
            return await db.Filings.FirstAsync(f => f.AccessionNumber == filing.AccessionNumber, ct);
        }
        return filing;
    }

    public async Task<FilingDelta> AddDeltaAsync(FilingDelta delta, CancellationToken ct = default)
    {
        delta.Symbol = delta.Symbol.ToUpperInvariant();
        delta.ScoredAt = DateTime.UtcNow;
        db.FilingDeltas.Add(delta);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(delta).State = EntityState.Detached;
            return await db.FilingDeltas.FirstAsync(d => d.FilingId == delta.FilingId, ct);
        }
        return delta;
    }

    public Task<FilingDelta?> GetLatestNonZeroDeltaAsync(string symbol, CancellationToken ct = default) =>
        db.FilingDeltas
            .Where(d => d.Symbol == symbol.ToUpper() && d.Delta != 0m)
            .OrderByDescending(d => d.FiledAt)
            .ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);

    public Task<List<FilingDelta>> GetDeltasSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
        db.FilingDeltas
            .Where(d => d.ScoredAt >= sinceUtc)
            .OrderBy(d => d.Delta) // worst first - the report leads with what needs eyes
            .ToListAsync(ct);

    public Task<List<FilingDeltaView>> GetDeltaViewsSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
        db.FilingDeltas
            .Where(d => d.ScoredAt >= sinceUtc)
            .Join(db.Filings, d => d.FilingId, f => f.Id,
                (d, f) => new FilingDeltaView(d, f.FilingType, f.Cik, f.AccessionNumber, f.PrimaryDocument))
            .ToListAsync(ct);

    public Task<int> CountFilingsSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
        db.Filings.CountAsync(f => f.CreatedAt >= sinceUtc, ct);

    public async Task<bool> AddDistressFlagAsync(DistressFlag flag, CancellationToken ct = default)
    {
        flag.Symbol = flag.Symbol.ToUpperInvariant();
        flag.CreatedAt = DateTime.UtcNow;
        // Idempotent per (accession, reason) so daily sync re-runs never
        // duplicate: the same 8-K seen tomorrow is the same flag.
        if (await db.DistressFlags.AnyAsync(
                d => d.AccessionNumber == flag.AccessionNumber && d.Reason == flag.Reason, ct))
            return false;
        db.DistressFlags.Add(flag);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(flag).State = EntityState.Detached; // racing sync inserted it first
            return false;
        }
        return true;
    }

    public Task<List<DistressFlag>> GetActiveDistressFlagsAsync(string symbol, DateOnly activeSince, CancellationToken ct = default) =>
        db.DistressFlags
            .Where(d => d.Symbol == symbol.ToUpper() && d.FiledAt >= activeSince)
            .OrderByDescending(d => d.FiledAt)
            .ToListAsync(ct);

    public Task<List<DistressFlag>> GetActiveDistressFlagsAsync(IReadOnlyCollection<string> symbols, DateOnly activeSince, CancellationToken ct = default)
    {
        var upper = symbols.Select(s => s.ToUpperInvariant()).ToList();
        return db.DistressFlags
            .Where(d => upper.Contains(d.Symbol) && d.FiledAt >= activeSince)
            .OrderByDescending(d => d.FiledAt)
            .ToListAsync(ct);
    }

    public Task<List<DistressFlag>> GetDistressFlagsSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
        db.DistressFlags
            .Where(d => d.CreatedAt >= sinceUtc)
            .OrderByDescending(d => d.FiledAt)
            .ToListAsync(ct);
}
