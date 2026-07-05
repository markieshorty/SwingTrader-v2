using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IReadinessSnapshotRepository
{
    Task UpsertAsync(ReadinessSnapshot snapshot);
    Task<List<ReadinessSnapshot>> GetRecentAsync(int accountId, int days = 30);
    Task<ReadinessSnapshot?> GetLatestAsync(int accountId);
}
