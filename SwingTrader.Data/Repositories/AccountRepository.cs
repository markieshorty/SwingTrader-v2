using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AccountRepository(SwingTraderDbContext db) : IAccountRepository
{
    public async Task<Account> CreateAsync(Account account, CancellationToken ct = default)
    {
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public Task<Account?> GetAsync(int accountId, CancellationToken ct = default) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);

    public async Task UpdateAsync(Account account, CancellationToken ct = default)
    {
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Excludes accounts with no real owner - the 'system' seed account
    // (SystemAccountId, from the original pre-multi-tenancy migration) and
    // any stray orphan account are never going to have API keys configured,
    // so the Scheduler enqueuing jobs for them just fails forever and
    // spams the admin Jobs tab. An account with zero AppUsers can never do
    // anything useful anyway - nobody exists to have set anything up.
    public Task<List<Account>> ListActiveAsync(CancellationToken ct = default) =>
        db.Accounts
            .Where(a => !a.IsDeleted && db.AppUsers.Any(u => u.AccountId == a.Id))
            .ToListAsync(ct);
}
