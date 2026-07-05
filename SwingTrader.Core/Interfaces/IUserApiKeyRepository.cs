using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IUserApiKeyRepository
{
    Task<UserApiKey?> GetAsync(int accountId, string provider, CancellationToken ct = default);
    Task<List<UserApiKey>> ListAsync(int accountId, CancellationToken ct = default);
    Task UpsertAsync(UserApiKey key, CancellationToken ct = default);
    Task DeleteAsync(int accountId, string provider, CancellationToken ct = default);
}
