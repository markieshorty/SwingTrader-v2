using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AccountRiskProfileRepository(SwingTraderDbContext db) : IAccountRiskProfileRepository
{
    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var alreadySeeded = await db.AccountRiskProfiles.AnyAsync(p => p.AccountId == accountId, ct);
        if (alreadySeeded) return;

        var now = DateTime.UtcNow;
        db.AccountRiskProfiles.Add(new AccountRiskProfile
        {
            AccountId = accountId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<AccountRiskProfile> GetAsync(int accountId, CancellationToken ct = default)
    {
        var profile = await db.AccountRiskProfiles.FirstOrDefaultAsync(p => p.AccountId == accountId, ct);
        if (profile is not null)
            return profile;

        await SeedDefaultAsync(accountId, ct);
        return await db.AccountRiskProfiles.FirstAsync(p => p.AccountId == accountId, ct);
    }

    public async Task UpdateAsync(AccountRiskProfile profile, CancellationToken ct = default)
    {
        profile.Validate();

        var existing = await db.AccountRiskProfiles.FirstOrDefaultAsync(
            p => p.AccountId == profile.AccountId, ct)
            ?? throw new InvalidOperationException($"No risk profile found for account {profile.AccountId}.");

        existing.LockedCapitalPct = profile.LockedCapitalPct;
        existing.MaxPositionPctOfActive = profile.MaxPositionPctOfActive;
        existing.MaxOpenPositions = profile.MaxOpenPositions;
        existing.DailyLossCircuitBreakerPct = profile.DailyLossCircuitBreakerPct;
        existing.Tier1UnlockMinTrades = profile.Tier1UnlockMinTrades;
        existing.Tier1UnlockMinWinRate = profile.Tier1UnlockMinWinRate;
        existing.Tier2UnlockMinTrades = profile.Tier2UnlockMinTrades;
        existing.Tier2UnlockMinWinRate = profile.Tier2UnlockMinWinRate;
        existing.MaxHoldDays = profile.MaxHoldDays;
        existing.TrailingActivationPct = profile.TrailingActivationPct;
        existing.TrailingDistancePct = profile.TrailingDistancePct;
        existing.EarningsGateDays = profile.EarningsGateDays;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task<AccountRiskProfile> ResetToDefaultsAsync(int accountId, CancellationToken ct = default)
    {
        var existing = await db.AccountRiskProfiles.FirstOrDefaultAsync(p => p.AccountId == accountId, ct);
        var defaults = new AccountRiskProfile();

        if (existing is null)
        {
            await SeedDefaultAsync(accountId, ct);
            return await db.AccountRiskProfiles.FirstAsync(p => p.AccountId == accountId, ct);
        }

        existing.LockedCapitalPct = defaults.LockedCapitalPct;
        existing.MaxPositionPctOfActive = defaults.MaxPositionPctOfActive;
        existing.MaxOpenPositions = defaults.MaxOpenPositions;
        existing.DailyLossCircuitBreakerPct = defaults.DailyLossCircuitBreakerPct;
        existing.Tier1UnlockMinTrades = defaults.Tier1UnlockMinTrades;
        existing.Tier1UnlockMinWinRate = defaults.Tier1UnlockMinWinRate;
        existing.Tier2UnlockMinTrades = defaults.Tier2UnlockMinTrades;
        existing.Tier2UnlockMinWinRate = defaults.Tier2UnlockMinWinRate;
        existing.MaxHoldDays = defaults.MaxHoldDays;
        existing.TrailingActivationPct = defaults.TrailingActivationPct;
        existing.TrailingDistancePct = defaults.TrailingDistancePct;
        existing.EarningsGateDays = defaults.EarningsGateDays;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return existing;
    }
}
