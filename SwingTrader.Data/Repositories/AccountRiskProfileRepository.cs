using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AccountRiskProfileRepository(SwingTraderDbContext db) : IAccountRiskProfileRepository
{
    // Display/seed order: the Default master book first, then the detected
    // regime books (defensive -> aggressive) when sorted.
    private static readonly MarketRegime[] AllRegimes =
        [MarketRegime.Default, MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear, MarketRegime.Crisis];

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
        // Master override: an enabled Default book governs every trade, ignoring
        // the detected regime entirely (live and in sims).
        var defaultBook = await db.AccountRiskProfiles
            .FirstOrDefaultAsync(p => p.AccountId == accountId && p.Regime == MarketRegime.Default && p.Enabled, ct);
        if (defaultBook is not null)
            return defaultBook;

        // Otherwise the active book = the one for the account's currently-detected
        // regime (Monitor keeps CurrentMarketRegime fresh). Defaults to Neutral.
        var regime = await db.Accounts
            .Where(a => a.Id == accountId)
            .Select(a => (MarketRegime?)a.CurrentMarketRegime)
            .FirstOrDefaultAsync(ct) ?? MarketRegime.Neutral;
        return await GetAsync(accountId, regime, ct);
    }

    // Whether the Default master book is enabled for this account (short-circuits
    // regime switching). Cheap indexed lookup for the backtest baseline snapshot.
    public Task<bool> IsDefaultRegimeEnabledAsync(int accountId, CancellationToken ct = default) =>
        db.AccountRiskProfiles.AnyAsync(p => p.AccountId == accountId && p.Regime == MarketRegime.Default && p.Enabled, ct);

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
        dest.Enabled = src.Enabled; // the Default book's master switch
        dest.LockedCapitalPct = src.LockedCapitalPct;
        dest.MaxOpenPositions = src.MaxOpenPositions;
        dest.DailyLossCircuitBreakerPct = src.DailyLossCircuitBreakerPct;
        dest.MaxHoldDays = src.MaxHoldDays;
        dest.TrailingActivationPct = src.TrailingActivationPct;
        dest.TrailingDistancePct = src.TrailingDistancePct;
        dest.EarningsGateDays = src.EarningsGateDays;
        dest.MinHoldDays = src.MinHoldDays;
        dest.MomentumHealthThreshold = src.MomentumHealthThreshold;
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
                p.FlatPositionPct = 0.08m; // 5 x 8% = 40% <= 45% deployable
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
                p.MaxOpenPositions = 2;
                p.FlatPositionPct = 0.05m; // 2 x 5% = 10% <= 20% deployable
                p.StopLossPct = 0.05m;
                p.TargetPct = 0.10m;
                p.MaxHoldDays = 8;
                p.MomentumHealthThreshold = 0.45m;
                p.AutopauseTrading = true;
                break;

            case MarketRegime.Crisis: // lockdown: minimal exposure, paused
                p.LockedCapitalPct = 0.90m;
                p.MaxOpenPositions = 1;
                p.FlatPositionPct = 0.05m; // 1 x 5% = 5% <= 10% deployable
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
