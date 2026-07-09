using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class TradeRepository(SwingTraderDbContext context) : ITradeRepository
{
    public Task<Trade?> GetByIdAsync(int accountId, int id) =>
        context.Trades.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public async Task<IEnumerable<Trade>> GetAllAsync(int accountId, TradingMode tradingMode) =>
        await context.Trades.Where(x => x.AccountId == accountId && x.TradingMode == tradingMode).ToListAsync();

    public async Task<IEnumerable<Trade>> GetOpenTradesAsync(int accountId, TradingMode tradingMode) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.Status == TradeStatus.Open)
            .ToListAsync();

    public async Task<IEnumerable<Trade>> GetBySymbolAsync(int accountId, TradingMode tradingMode, string symbol) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.Symbol == symbol.ToUpperInvariant())
            .OrderByDescending(x => x.OpenedAt)
            .ToListAsync();

    public async Task<IEnumerable<Trade>> GetTradeHistoryAsync(int accountId, TradingMode tradingMode, DateTime from, DateTime to) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.OpenedAt >= from && x.OpenedAt <= to)
            .OrderByDescending(x => x.OpenedAt)
            .ToListAsync();

    public async Task<IEnumerable<Trade>> GetClosedOnDateAsync(int accountId, TradingMode tradingMode, DateOnly date)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        return await context.Trades
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.Status == TradeStatus.Closed
                && x.ClosedAt != null && x.ClosedAt >= start && x.ClosedAt < end)
            .ToListAsync();
    }

    public async Task<IEnumerable<Trade>> GetPendingTradesAsync(int accountId, TradingMode tradingMode) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.Status == TradeStatus.Pending)
            .ToListAsync();

    public async Task<IEnumerable<Trade>> GetUnreconciledOrdersAsync(int accountId, TradingMode tradingMode) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode &&
                ((x.EntryOrderId != null && x.EntryFillConfirmedAt == null) ||
                 (x.ExitOrderId != null && x.ExitFillConfirmedAt == null)))
            .ToListAsync();

    public async Task<Trade> AddAsync(Trade trade)
    {
        trade.Symbol = trade.Symbol.ToUpperInvariant();
        context.Trades.Add(trade);
        await context.SaveChangesAsync();
        return trade;
    }

    public async Task UpdateAsync(Trade trade)
    {
        trade.UpdatedAt = DateTime.UtcNow;
        context.Trades.Update(trade);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int accountId, int id)
    {
        var trade = await context.Trades.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);
        if (trade is not null)
        {
            context.Trades.Remove(trade);
            await context.SaveChangesAsync();
        }
    }
}
