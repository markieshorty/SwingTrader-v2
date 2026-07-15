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
        SetupType.OversoldRecovery, SetupType.Breakout, SetupType.MomentumContinuation,
        SetupType.VolumeSpike, SetupType.TrendFollowing,
    ];

    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var existing = await db.SetupTactics
            .Where(t => t.AccountId == accountId)
            .Select(t => t.SetupType)
            .ToListAsync(ct);

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
            db.SetupTactics.Add(tactics);
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    public Task<SetupTactics?> GetAsync(int accountId, SetupType setupType, CancellationToken ct = default) =>
        db.SetupTactics.FirstOrDefaultAsync(t => t.AccountId == accountId && t.SetupType == setupType, ct);

    public async Task<List<SetupTactics>> GetAllAsync(int accountId, CancellationToken ct = default)
    {
        var rows = await db.SetupTactics.Where(t => t.AccountId == accountId).ToListAsync(ct);
        if (rows.Count < TradableSetups.Length)
        {
            await SeedDefaultAsync(accountId, ct);
            rows = await db.SetupTactics.Where(t => t.AccountId == accountId).ToListAsync(ct);
        }
        return rows.OrderBy(t => Array.IndexOf(TradableSetups, t.SetupType)).ToList();
    }

    public async Task UpdateAsync(SetupTactics tactics, CancellationToken ct = default)
    {
        tactics.Validate();

        var existing = await db.SetupTactics.FirstOrDefaultAsync(
            t => t.AccountId == tactics.AccountId && t.SetupType == tactics.SetupType, ct)
            ?? throw new InvalidOperationException(
                $"No {tactics.SetupType} tactics found for account {tactics.AccountId}.");

        existing.StopLossPct = tactics.StopLossPct;
        existing.TargetPct = tactics.TargetPct;
        existing.GuideHoldDays = tactics.GuideHoldDays;
        existing.TrailingActivationPct = tactics.TrailingActivationPct;
        existing.TrailingDistancePct = tactics.TrailingDistancePct;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
