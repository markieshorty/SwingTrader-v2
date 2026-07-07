using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IActivityLogRepository
{
    Task LogAsync(int accountId, string category, string title, string result, string? message = null, CancellationToken ct = default);
    Task<IEnumerable<ActivityLog>> GetRecentAsync(int accountId, int limit = 200, CancellationToken ct = default);
}
