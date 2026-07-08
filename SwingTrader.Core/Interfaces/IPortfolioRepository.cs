using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IPortfolioRepository
{
    Task<PortfolioSnapshot?> GetByIdAsync(int accountId, int id);

    // tradingMode is required, not optional - Demo and Live balances are
    // entirely unrelated numbers. A caller reading "latest"/"today's"
    // snapshot without pinning it to the account's current mode risks
    // comparing across the two (confirmed live: this is exactly what let
    // the circuit breaker misfire when an account switched Demo -> Live
    // mid-day, reading the mode switch as a ~100% drawdown).
    Task<PortfolioSnapshot?> GetLatestSnapshotAsync(int accountId, TradingMode tradingMode);
    Task<IEnumerable<PortfolioSnapshot>> GetSnapshotHistoryAsync(int accountId, TradingMode tradingMode, DateOnly from, DateOnly to);
    Task<PortfolioSnapshot> AddAsync(PortfolioSnapshot snapshot);
    Task UpdateAsync(PortfolioSnapshot snapshot);
    Task DeleteAsync(int accountId, int id);
}
