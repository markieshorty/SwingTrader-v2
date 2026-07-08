using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(int accountId, int id);
    Task<IEnumerable<Trade>> GetAllAsync(int accountId);
    Task<IEnumerable<Trade>> GetOpenTradesAsync(int accountId);
    Task<IEnumerable<Trade>> GetBySymbolAsync(int accountId, string symbol);
    Task<IEnumerable<Trade>> GetTradeHistoryAsync(int accountId, DateTime from, DateTime to);

    // Trades with an EntryOrderId/ExitOrderId still awaiting fill
    // confirmation from T212 - see Trade.EntryFillConfirmedAt.
    Task<IEnumerable<Trade>> GetUnreconciledOrdersAsync(int accountId);
    Task<Trade> AddAsync(Trade trade);
    Task UpdateAsync(Trade trade);
    Task DeleteAsync(int accountId, int id);
}
