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

    // Trades that closed on the given calendar day (by ClosedAt, not
    // OpenedAt - a position exited today could have been opened days ago).
    // Used by ExecutionService to stop a same-day re-enqueue (after an exit
    // frees capital) from immediately re-buying the exact symbol it just
    // sold - resets naturally the next day.
    Task<IEnumerable<Trade>> GetClosedOnDateAsync(int accountId, DateOnly date);
    Task<Trade> AddAsync(Trade trade);
    Task UpdateAsync(Trade trade);
    Task DeleteAsync(int accountId, int id);
}
