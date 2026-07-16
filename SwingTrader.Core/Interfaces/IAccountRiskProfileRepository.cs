using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAccountRiskProfileRepository
{
    // Seeds all four regime books (Bull/Neutral/Bear/Crisis) for a new account.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);

    // The ACTIVE risk book - the one for the account's currently-detected
    // regime (Account.CurrentMarketRegime, kept fresh by Monitor). Live
    // consumers call this and are regime-aware for free. Seeds the books first
    // if none exist, so callers never null-check.
    Task<AccountRiskProfile> GetAsync(int accountId, CancellationToken ct = default);

    // A specific regime's book (for the settings UI editing one book).
    Task<AccountRiskProfile> GetAsync(int accountId, MarketRegime regime, CancellationToken ct = default);

    // All risk books (Default + Bull/Neutral/Bear/Crisis), Default first.
    Task<List<AccountRiskProfile>> GetAllAsync(int accountId, CancellationToken ct = default);

    // True when the Default master book is enabled - it overrides the detected
    // regime for every trade (live and in sims).
    Task<bool> IsDefaultRegimeEnabledAsync(int accountId, CancellationToken ct = default);

    // Calls profile.Validate() before saving; keyed on (AccountId, Regime).
    Task UpdateAsync(AccountRiskProfile profile, CancellationToken ct = default);

    // Resets a single regime book to its seeded default posture.
    Task<AccountRiskProfile> ResetToDefaultsAsync(int accountId, MarketRegime regime, CancellationToken ct = default);
}
