using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(int accountId, int id);
    Task<IEnumerable<Trade>> GetAllAsync(int accountId);
    Task<IEnumerable<Trade>> GetOpenTradesAsync(int accountId);
    Task<IEnumerable<Trade>> GetBySymbolAsync(int accountId, string symbol);
    Task<IEnumerable<Trade>> GetTradeHistoryAsync(int accountId, DateTime from, DateTime to);
    Task<Trade> AddAsync(Trade trade);
    Task UpdateAsync(Trade trade);
    Task DeleteAsync(int accountId, int id);
}
