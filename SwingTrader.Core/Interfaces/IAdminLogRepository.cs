using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAdminLogRepository
{
    Task LogAsync(AdminActionLog entry, CancellationToken ct = default);
    Task<List<AdminActionLog>> GetRecentAsync(int count = 200, CancellationToken ct = default);
}
