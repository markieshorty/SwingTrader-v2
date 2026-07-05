using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAccountRepository
{
    Task<Account> CreateAsync(Account account, CancellationToken ct = default);
    Task<Account?> GetAsync(int accountId, CancellationToken ct = default);
    Task UpdateAsync(Account account, CancellationToken ct = default);
    // Used by the Scheduler to enumerate accounts to check for job windows.
    Task<List<Account>> ListActiveAsync(CancellationToken ct = default);
}
