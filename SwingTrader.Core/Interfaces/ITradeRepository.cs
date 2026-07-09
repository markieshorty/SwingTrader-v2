using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ITradeRepository
{
    // Not scoped by TradingMode - the id already pins the row uniquely.
    Task<Trade?> GetByIdAsync(int accountId, int id);

    // tradingMode is required, not optional - Demo and Live trades for the
    // same account must never mix in a single result set (see
    // PortfolioSnapshot.TradingMode).
    Task<IEnumerable<Trade>> GetAllAsync(int accountId, TradingMode tradingMode);
    Task<IEnumerable<Trade>> GetOpenTradesAsync(int accountId, TradingMode tradingMode);
    Task<IEnumerable<Trade>> GetBySymbolAsync(int accountId, TradingMode tradingMode, string symbol);
    Task<IEnumerable<Trade>> GetTradeHistoryAsync(int accountId, TradingMode tradingMode, DateTime from, DateTime to);

    // Trades with an EntryOrderId/ExitOrderId still awaiting fill
    // confirmation from T212 - see Trade.EntryFillConfirmedAt.
    Task<IEnumerable<Trade>> GetUnreconciledOrdersAsync(int accountId, TradingMode tradingMode);

    // Intent-first placements written before the broker call whose order
    // outcome is still unknown (Status == Pending, no EntryOrderId yet).
    // Monitor resolves these against T212 order history - see MonitorService's
    // pending reconciliation.
    Task<IEnumerable<Trade>> GetPendingTradesAsync(int accountId, TradingMode tradingMode);

    // Trades that closed on the given calendar day (by ClosedAt, not
    // OpenedAt - a position exited today could have been opened days ago).
    // Used by ExecutionService to stop a same-day re-enqueue (after an exit
    // frees capital) from immediately re-buying the exact symbol it just
    // sold - resets naturally the next day.
    Task<IEnumerable<Trade>> GetClosedOnDateAsync(int accountId, TradingMode tradingMode, DateOnly date);
    Task<Trade> AddAsync(Trade trade);
    Task UpdateAsync(Trade trade);
    Task DeleteAsync(int accountId, int id);
}
