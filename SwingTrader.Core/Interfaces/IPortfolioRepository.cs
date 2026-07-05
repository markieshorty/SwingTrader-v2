using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IPortfolioRepository
{
    Task<PortfolioSnapshot?> GetByIdAsync(int accountId, int id);
    Task<PortfolioSnapshot?> GetLatestSnapshotAsync(int accountId);
    Task<IEnumerable<PortfolioSnapshot>> GetSnapshotHistoryAsync(int accountId, DateOnly from, DateOnly to);
    Task<PortfolioSnapshot> AddAsync(PortfolioSnapshot snapshot);
    Task UpdateAsync(PortfolioSnapshot snapshot);
    Task DeleteAsync(int accountId, int id);
}
