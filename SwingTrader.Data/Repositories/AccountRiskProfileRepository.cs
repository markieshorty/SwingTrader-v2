using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AccountRiskProfileRepository(SwingTraderDbContext db) : IAccountRiskProfileRepository
{
    // Display/seed order, defensive -> aggressive left to right when sorted.
    private static readonly MarketRegime[] AllRegimes =
        [MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear, MarketRegime.Crisis];

    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var existing = await db.AccountRiskProfiles
            .Where(p => p.AccountId == accountId)
            .Select(p => p.Regime)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var added = false;
        foreach (var regime in AllRegimes)
        {
            if (existing.Contains(regime)) continue;
            var profile = DefaultForRegime(regime);
            profile.AccountId = accountId;
            profile.CreatedAt = now;
            profile.UpdatedAt = now;
            db.AccountRiskProfiles.Add(profile);
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    public async Task<AccountRiskProfile> GetAsync(int accountId, CancellationToken ct = default)
    {
        // The active book = the one for the account's currently-detected regime
        // (Monitor keeps CurrentMarketRegime fresh). Defaults to Neutral.
        var regime = await db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => (MarketRegime?)a.CurrentMarketRegime)
            .FirstOrDefaultAsync(ct) ?? MarketRegime.Neutral;
        return await GetAsync(accountId, regime, ct);
    }

    public async Task<AccountRiskProfile> GetAsync(int accountId, MarketRegime regime, CancellationToken ct = default)
    {
        var profile = await db.AccountRiskProfiles
            .FirstOrDefaultAsync(p => p.AccountId == accountId && p.Regime == regime, ct);
        if (profile is not null)
            return profile;

        // Missing book (new account, or a pre-regime account being upgraded):
        // seed whatever's absent, then return the requested one.
        await SeedDefaultAsync(accountId, ct);
        return await db.AccountRiskProfiles
            .FirstAsync(p => p.AccountId == accountId && p.Regime == regime, ct);
    }

    public async Task<List<AccountRiskProfile>> GetAllAsync(int accountId, CancellationToken ct = default)
    {
        var books = await db.AccountRiskProfiles
            .Where(p => p.AccountId == accountId)
            .ToListAsync(ct);

        if (books.Count < AllRegimes.Length)
        {
            await SeedDefaultAsync(accountId, ct);
            books = await db.AccountRiskProfiles
                .Where(p => p.AccountId == accountId)
                .ToListAsync(ct);
        }

        return books.OrderBy(p => Array.IndexOf(AllRegimes, p.Regime)).ToList();
    }

    public async Task UpdateAsync(AccountRiskProfile profile, CancellationToken ct = default)
    {
        profile.Validate();

        var existing = await db.AccountRiskProfiles.FirstOrDefaultAsync(
            p => p.AccountId == profile.AccountId && p.Regime == profile.Regime, ct)
            ?? throw new InvalidOperationException(
                $"No {profile.Regime} risk book found for account {profile.AccountId}.");

        CopySettings(profile, existing);
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AccountRiskProfile> ResetToDefaultsAsync(int accountId, MarketRegime regime, CancellationToken ct = default)
    {
        var existing = await db.AccountRiskProfiles.FirstOrDefaultAsync(
            p => p.AccountId == accountId && p.Regime == regime, ct);
        if (existing is null)
        {
            await SeedDefaultAsync(accountId, ct);
            return await db.AccountRiskProfiles.FirstAsync(
                p => p.AccountId == accountId && p.Regime == regime, ct);
        }

        CopySettings(DefaultForRegime(regime), existing);
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    // Copies every editable setting (all but identity / Regime / audit stamps).
    private static void CopySettings(AccountRiskProfile src, AccountRiskProfile dest)
    {
        dest.LockedCapitalPct = src.LockedCapitalPct;
        dest.MaxPositionPctOfActive = src.MaxPositionPctOfActive;
        dest.MaxOpenPositions = src.MaxOpenPositions;
        dest.DailyLossCircuitBreakerPct = src.DailyLossCircuitBreakerPct;
        dest.Tier1UnlockMinTrades = src.Tier1UnlockMinTrades;
        dest.Tier1UnlockMinWinRate = src.Tier1UnlockMinWinRate;
        dest.Tier2UnlockMinTrades = src.Tier2UnlockMinTrades;
        dest.Tier2UnlockMinWinRate = src.Tier2UnlockMinWinRate;
        dest.MaxHoldDays = src.MaxHoldDays;
        dest.TrailingActivationPct = src.TrailingActivationPct;
        dest.TrailingDistancePct = src.TrailingDistancePct;
        dest.EarningsGateDays = src.EarningsGateDays;
        dest.MinHoldDays = src.MinHoldDays;
        dest.MomentumHealthThreshold = src.MomentumHealthThreshold;
        dest.TargetWatchlistSize = src.TargetWatchlistSize;
        dest.AutopauseTrading = src.AutopauseTrading;
        dest.StopLossPct = src.StopLossPct;
        dest.TargetPct = src.TargetPct;
        dest.SizingMode = src.SizingMode;
        dest.FlatPositionPct = src.FlatPositionPct;
        dest.SizingAggressiveness = src.SizingAggressiveness;
        dest.ForwardVetoFloor = src.ForwardVetoFloor;
    }

    // Seeded posture per regime: risk appetite rises Crisis -> Bull. Neutral is
    // the pre-regime baseline (model defaults). All values stay within
    // CapitalRules bounds and pass AccountRiskProfile.Validate(); they are
    // starting points the owner tunes per book (Phase 2 optimizes them).
    public static AccountRiskProfile DefaultForRegime(MarketRegime regime)
    {
        var p = new AccountRiskProfile { Regime = regime };
        switch (regime)
        {
            case MarketRegime.Bull: // aggressive: more positions, room to run
                p.LockedCapitalPct = 0.55m;
                p.MaxOpenPositions = 5;
                p.StopLossPct = 0.08m;
                p.TargetPct = 0.20m;
                p.MaxHoldDays = 20;
                p.MomentumHealthThreshold = 0.30m;
                p.AutopauseTrading = false;
                break;

            case MarketRegime.Neutral: // model defaults - the baseline book
                p.AutopauseTrading = false;
                break;

            case MarketRegime.Bear: // defensive: less exposure, cut faster, paused
                p.LockedCapitalPct = 0.80m;
                p.MaxPositionPctOfActive = 0.15m;
                p.MaxOpenPositions = 2;
                p.StopLossPct = 0.05m;
                p.TargetPct = 0.10m;
                p.MaxHoldDays = 8;
                p.MomentumHealthThreshold = 0.45m;
                p.AutopauseTrading = true;
                break;

            case MarketRegime.Crisis: // lockdown: minimal exposure, paused
                p.LockedCapitalPct = 0.90m;
                p.MaxPositionPctOfActive = 0.10m;
                p.MaxOpenPositions = 1;
                p.StopLossPct = 0.05m;
                p.TargetPct = 0.10m;
                p.MaxHoldDays = 8;
                p.MomentumHealthThreshold = 0.50m;
                p.AutopauseTrading = true;
                break;
        }
        return p;
    }
}
