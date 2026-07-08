using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IReportRepository
{
    Task<DailyReport?> GetByIdAsync(int accountId, int id);
    Task<DailyReport?> GetByDateAsync(int accountId, TradingMode tradingMode, DateOnly date);
    Task<IEnumerable<DailyReport>> GetAllAsync(int accountId, TradingMode tradingMode);
    Task<IEnumerable<DailyReport>> GetUnsentReportsAsync(int accountId, TradingMode tradingMode);
    Task<DailyReport> AddAsync(DailyReport report);
    Task UpdateAsync(DailyReport report);
    Task DeleteAsync(int accountId, int id);
}
