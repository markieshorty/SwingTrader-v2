using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IUserRepository
{
    Task<AppUser?> FindAsync(string userId, CancellationToken ct = default);
    Task<AppUser> CreateAsync(AppUser user, CancellationToken ct = default);
    Task UpdateLastLoginAsync(string userId, CancellationToken ct = default);
    Task<List<AppUser>> ListByAccountAsync(int accountId, CancellationToken ct = default);
    Task RemoveAsync(string userId, CancellationToken ct = default);
    Task ApproveAsync(string userId, CancellationToken ct = default);
}
