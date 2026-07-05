using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAccountRiskProfileRepository
{
    // Seeds the default risk profile row for a brand-new account.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);

    // Returns the default profile (seeding it first) if none exists yet -
    // callers should never have to null-check a risk profile.
    Task<AccountRiskProfile> GetAsync(int accountId, CancellationToken ct = default);

    // Calls profile.Validate() before saving.
    Task UpdateAsync(AccountRiskProfile profile, CancellationToken ct = default);

    Task<AccountRiskProfile> ResetToDefaultsAsync(int accountId, CancellationToken ct = default);
}
