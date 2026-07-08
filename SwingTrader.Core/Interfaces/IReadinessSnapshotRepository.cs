using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IReadinessSnapshotRepository
{
    Task UpsertAsync(ReadinessSnapshot snapshot);
    Task<List<ReadinessSnapshot>> GetRecentAsync(int accountId, TradingMode tradingMode, int days = 30);
    Task<ReadinessSnapshot?> GetLatestAsync(int accountId, TradingMode tradingMode);
}
