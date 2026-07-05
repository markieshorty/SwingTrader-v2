using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ISignalRepository
{
    Task<StockSignal?> GetByIdAsync(int accountId, int id);
    Task<IEnumerable<StockSignal>> GetAllAsync(int accountId);
    Task<IEnumerable<StockSignal>> GetByDateAsync(int accountId, DateOnly date);
    Task<IEnumerable<StockSignal>> GetUnexecutedSignalsAsync(int accountId);
    Task<IEnumerable<StockSignal>> GetBySymbolAsync(int accountId, string symbol);
    Task<StockSignal> AddAsync(StockSignal signal);
    Task UpdateAsync(StockSignal signal);
    Task<StockSignal> UpsertAsync(StockSignal signal);
    Task DeleteAsync(int accountId, int id);
}
