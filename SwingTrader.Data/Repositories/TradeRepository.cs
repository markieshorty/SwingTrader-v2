using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class TradeRepository(SwingTraderDbContext context) : ITradeRepository
{
    public Task<Trade?> GetByIdAsync(int accountId, int id) =>
        context.Trades.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public async Task<IEnumerable<Trade>> GetAllAsync(int accountId) =>
        await context.Trades.Where(x => x.AccountId == accountId).ToListAsync();

    public async Task<IEnumerable<Trade>> GetOpenTradesAsync(int accountId) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.Status == TradeStatus.Open)
            .ToListAsync();

    public async Task<IEnumerable<Trade>> GetBySymbolAsync(int accountId, string symbol) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.Symbol == symbol.ToUpperInvariant())
            .OrderByDescending(x => x.OpenedAt)
            .ToListAsync();

    public async Task<IEnumerable<Trade>> GetTradeHistoryAsync(int accountId, DateTime from, DateTime to) =>
        await context.Trades
            .Where(x => x.AccountId == accountId && x.OpenedAt >= from && x.OpenedAt <= to)
            .OrderByDescending(x => x.OpenedAt)
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
