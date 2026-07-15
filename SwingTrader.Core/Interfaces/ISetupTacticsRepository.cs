using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ISetupTacticsRepository
{
    // Seeds one tactics row per tradable setup for a new account, copying the
    // account's Neutral risk book so behaviour is continuous until tuned.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);

    // The tactics for a specific setup, or null if none (caller falls back to
    // the risk profile - e.g. an Unknown setup that was never seeded).
    Task<SetupTactics?> GetAsync(int accountId, SetupType setupType, CancellationToken ct = default);

    // All setup rows for the account (seeding first if missing).
    Task<List<SetupTactics>> GetAllAsync(int accountId, CancellationToken ct = default);

    // Calls tactics.Validate() before saving; keyed on (AccountId, SetupType).
    Task UpdateAsync(SetupTactics tactics, CancellationToken ct = default);
}
