using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class ShadowOutcomeRepository(SwingTraderDbContext db) : IShadowOutcomeRepository
{
    // Mirrors IX_ShadowOutcomes_Identity. Kept in one place so the skip-set and
    // the upsert can never disagree about what "already replayed" means.
    public static string Key(ShadowOutcome o) =>
        Key(o.Symbol, o.SignalDate, (int)o.SetupType);

    private static string Key(string symbol, DateOnly date, int setup) =>
        $"{symbol}|{date:yyyyMMdd}|{setup}";

    public async Task<HashSet<string>> GetStoredKeysAsync(
        string dialSetVersion, int datasetVersion, CancellationToken ct = default)
    {
        // Projection, not entities: a full backfill compares against every
        // stored row, and materialising those on the Basic tier is the
        // difference between a fast skip and a 300s timeout.
        var rows = await db.ShadowOutcomes
            .Where(o => o.DialSetVersion == dialSetVersion && o.DatasetVersion == datasetVersion)
            .Select(o => new { o.Symbol, o.SignalDate, o.SetupType })
            .ToListAsync(ct);

        return rows.Select(r => Key(r.Symbol, r.SignalDate, (int)r.SetupType))
                   .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<int> UpsertRangeAsync(
        IReadOnlyCollection<ShadowOutcome> outcomes, CancellationToken ct = default)
    {
        if (outcomes.Count == 0) return 0;

        var version = outcomes.First().DialSetVersion;
        var dataset = outcomes.First().DatasetVersion;
        if (outcomes.Any(o => o.DialSetVersion != version || o.DatasetVersion != dataset))
            throw new ArgumentException(
                "A batch must share one dial set and dataset version - the existing-row lookup keys on both.",
                nameof(outcomes));

        var symbols = outcomes.Select(o => o.Symbol).Distinct().ToList();
        var existing = await db.ShadowOutcomes
            .Where(o => o.DialSetVersion == version && o.DatasetVersion == dataset
                        && symbols.Contains(o.Symbol))
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(Key, StringComparer.Ordinal);

        var written = 0;
        foreach (var o in outcomes)
        {
            if (byKey.TryGetValue(Key(o), out var row))
            {
                row.Source = o.Source;
                row.SignalId = o.SignalId;
                row.Membership = o.Membership;
                row.ReplayedAt = o.ReplayedAt;
                row.StopLossPct = o.StopLossPct;
                row.TargetPct = o.TargetPct;
                row.GuideHoldDays = o.GuideHoldDays;
                row.TrailingActivationPct = o.TrailingActivationPct;
                row.TrailingDistancePct = o.TrailingDistancePct;
                row.EntryDate = o.EntryDate;
                row.EntryPrice = o.EntryPrice;
                row.ExitDate = o.ExitDate;
                row.ExitPrice = o.ExitPrice;
                row.ExitReason = o.ExitReason;
                row.ReturnPct = o.ReturnPct;
                row.TradingDaysHeld = o.TradingDaysHeld;
                row.StillOpen = o.StillOpen;
                row.Fwd5Pct = o.Fwd5Pct;
                row.Fwd20Pct = o.Fwd20Pct;
                row.Fwd40Pct = o.Fwd40Pct;
                row.MaxFavorablePct = o.MaxFavorablePct;
                row.MaxAdversePct = o.MaxAdversePct;
                row.HitPlus25Within40 = o.HitPlus25Within40;
                row.HitMinus25Within40 = o.HitMinus25Within40;
                row.SectorFwd40Pct = o.SectorFwd40Pct;
                row.SectorMoveAtSignalPct = o.SectorMoveAtSignalPct;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.ShadowOutcomes.Add(o);
            }
            written++;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Detach everything this batch added, so a rejected row cannot ride
            // along on the caller's next SaveChanges and bury the real error
            // (7 Aug 2026 - the filing-events dead-letter hunt).
            foreach (var entry in db.ChangeTracker.Entries<ShadowOutcome>()
                         .Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
            throw;
        }
        return written;
    }

    public Task<List<ShadowOutcome>> GetForCalibrationAsync(
        string dialSetVersion, int datasetVersion, CancellationToken ct = default) =>
        db.ShadowOutcomes
            .Where(o => o.DialSetVersion == dialSetVersion && o.DatasetVersion == datasetVersion
                        // The calibration target is a complete 40-bar window.
                        // Partial windows understate both tails.
                        && o.HitPlus25Within40 != null)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<int> CountAsync(string dialSetVersion, int datasetVersion, CancellationToken ct = default) =>
        db.ShadowOutcomes.CountAsync(
            o => o.DialSetVersion == dialSetVersion && o.DatasetVersion == datasetVersion, ct);
}
