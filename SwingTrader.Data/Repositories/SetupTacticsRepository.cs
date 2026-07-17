using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class SetupTacticsRepository(SwingTraderDbContext db) : ISetupTacticsRepository
{
    // The setups that actually produce Buy signals (Unknown never trades).
    private static readonly SetupType[] TradableSetups =
    [
        SetupType.OversoldRecovery, SetupType.OversoldRecoveryLoose, SetupType.Breakout,
        SetupType.MomentumContinuation, SetupType.VolumeSpike, SetupType.TrendFollowing,
    ];

    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var existingRows = await db.SetupTactics
            .Where(t => t.AccountId == accountId)
            .ToListAsync(ct);
        var existing = existingRows.Select(t => t.SetupType).ToList();

        // Continuity: copy the account's Neutral risk book onto every setup so
        // exit behaviour is unchanged until the owner differentiates them.
        var neutral = await db.AccountRiskProfiles
            .FirstOrDefaultAsync(p => p.AccountId == accountId && p.Regime == MarketRegime.Neutral, ct);

        var now = DateTime.UtcNow;
        var added = false;
        foreach (var setup in TradableSetups)
        {
            if (existing.Contains(setup)) continue;
            var tactics = new SetupTactics
            {
                AccountId = accountId, SetupType = setup, CreatedAt = now, UpdatedAt = now,
            };
            if (neutral is not null)
            {
                tactics.StopLossPct = neutral.StopLossPct;
                tactics.TargetPct = neutral.TargetPct;
                tactics.GuideHoldDays = neutral.MaxHoldDays;
                tactics.TrailingActivationPct = neutral.TrailingActivationPct;
                tactics.TrailingDistancePct = neutral.TrailingDistancePct;
            }
            // OversoldRecoveryLoose (the unconfirmed variant split out 17 Jul
            // 2026): inherit the confirmed setup's tactics when it has a row -
            // they were one setup until the split - and seed DISABLED so the
            // aggressive variant never starts trading without the owner
            // explicitly switching it on in Settings -> Setup tactics.
            if (setup == SetupType.OversoldRecoveryLoose)
            {
                if (existingRows.FirstOrDefault(t => t.SetupType == SetupType.OversoldRecovery) is { } confirmed)
                {
                    tactics.StopLossPct = confirmed.StopLossPct;
                    tactics.TargetPct = confirmed.TargetPct;
                    tactics.GuideHoldDays = confirmed.GuideHoldDays;
                    tactics.TrailingActivationPct = confirmed.TrailingActivationPct;
                    tactics.TrailingDistancePct = confirmed.TrailingDistancePct;
                }
                tactics.Enabled = false;
            }
            db.SetupTactics.Add(tactics);
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    public Task<SetupTactics?> GetAsync(int accountId, SetupType setupType, CancellationToken ct = default) =>
        db.SetupTactics.FirstOrDefaultAsync(t => t.AccountId == accountId && t.SetupType == setupType, ct);

    // Setups switched OFF for live trading. Read once per research symbol to
    // demote their Buys to Watch; a tiny (~5-row) indexed lookup. A setup with
    // no row is treated as enabled (default), matching the model default.
    public async Task<HashSet<SetupType>> GetDisabledSetupsAsync(int accountId, CancellationToken ct = default)
    {
        var disabled = await db.SetupTactics
            .Where(t => t.AccountId == accountId && !t.Enabled)
            .Select(t => t.SetupType)
            .ToListAsync(ct);
        return disabled.ToHashSet();
    }

    public async Task<List<SetupTactics>> GetAllAsync(int accountId, CancellationToken ct = default)
    {
        var rows = await db.SetupTactics.Where(t => t.AccountId == accountId).ToListAsync(ct);
        // Reseed if any tradable setup is MISSING - a plain count check misses
        // the case where a stray non-tradable row (e.g. a mis-seeded Unknown)
        // pads the count to 5 while TrendFollowing is absent.
        if (TradableSetups.Any(s => rows.All(r => r.SetupType != s)))
        {
            await SeedDefaultAsync(accountId, ct);
            rows = await db.SetupTactics.Where(t => t.AccountId == accountId).ToListAsync(ct);
        }
        // Only tradable setups surface in the editor - a non-tradable row (like
        // Unknown, which never trades) is filtered out rather than rendered as a
        // blank-labelled row.
        return rows.Where(r => TradableSetups.Contains(r.SetupType))
            .OrderBy(t => Array.IndexOf(TradableSetups, t.SetupType))
            .ToList();
    }

    public async Task UpdateAsync(SetupTactics tactics, CancellationToken ct = default)
    {
        tactics.Validate();

        var existing = await db.SetupTactics.FirstOrDefaultAsync(
            t => t.AccountId == tactics.AccountId && t.SetupType == tactics.SetupType, ct)
            ?? throw new InvalidOperationException(
                $"No {tactics.SetupType} tactics found for account {tactics.AccountId}.");

        existing.Enabled = tactics.Enabled;
        existing.StopLossPct = tactics.StopLossPct;
        existing.TargetPct = tactics.TargetPct;
        existing.GuideHoldDays = tactics.GuideHoldDays;
        existing.TrailingActivationPct = tactics.TrailingActivationPct;
        existing.TrailingDistancePct = tactics.TrailingDistancePct;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
