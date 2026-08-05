using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAccountAllocationRepository
{
    // Never null: accounts without a row get the inert default (Swing 100%).
    Task<AccountAllocation> GetAsync(int accountId, CancellationToken ct = default);

    // Validates before saving.
    Task<AccountAllocation> UpsertAsync(AccountAllocation allocation, CancellationToken ct = default);
}
