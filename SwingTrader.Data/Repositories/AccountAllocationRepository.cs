using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AccountAllocationRepository(SwingTraderDbContext db) : IAccountAllocationRepository
{
    public async Task<AccountAllocation> GetAsync(int accountId, CancellationToken ct = default) =>
        await db.AccountAllocations.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == accountId, ct)
            ?? new AccountAllocation { AccountId = accountId }; // inert default: Swing 100%

    public async Task<AccountAllocation> UpsertAsync(AccountAllocation allocation, CancellationToken ct = default)
    {
        allocation.Validate();
        var existing = await db.AccountAllocations.FirstOrDefaultAsync(a => a.AccountId == allocation.AccountId, ct);
        if (existing is null)
        {
            db.AccountAllocations.Add(allocation);
        }
        else
        {
            existing.SpyCorePct = allocation.SpyCorePct;
            existing.FactorTiltPct = allocation.FactorTiltPct;
            existing.SwingPct = allocation.SwingPct;
            existing.CoreTicker = allocation.CoreTicker;
            existing.UpdatedAt = DateTime.UtcNow;
            allocation = existing;
        }
        await db.SaveChangesAsync(ct);
        return allocation;
    }
}
