using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAccountRepository
{
    Task<Account> CreateAsync(Account account, CancellationToken ct = default);
    Task<Account?> GetAsync(int accountId, CancellationToken ct = default);
}
