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

    public Task<List<Account>> ListActiveAsync(CancellationToken ct = default) =>
        db.Accounts.Where(a => !a.IsDeleted).ToListAsync(ct);
}
