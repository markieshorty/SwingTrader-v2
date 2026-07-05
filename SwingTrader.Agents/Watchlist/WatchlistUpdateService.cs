using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Watchlist;

public class WatchlistUpdateService(
    IWatchlistRepository watchlist,
    IWatchlistHistoryRepository history,
    ITradeRepository trades,
    ILogger<WatchlistUpdateService> logger) : IWatchlistUpdateService
{
    public async Task UpdateAsync(int accountId, List<WatchlistSelection> selections, CancellationToken ct = default)
    {
        var weekStarting = NextMonday();
        var newSymbols = selections.Select(s => s.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 1 — current active list
        var currentActive = (await watchlist.GetActiveAsync(accountId)).ToList();
        var currentSymbols = currentActive.Select(w => w.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 7 — protect open positions from removal
        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 3 — diff
        var toRemove = currentSymbols.Except(newSymbols, StringComparer.OrdinalIgnoreCase)
            .Where(s => !openTradeSymbols.Contains(s))
            .ToList();
        var toAdd = newSymbols.Except(currentSymbols, StringComparer.OrdinalIgnoreCase).ToList();
        var kept = currentSymbols.Intersect(newSymbols, StringComparer.OrdinalIgnoreCase).Count();

        // Step 4 — removals
        foreach (var symbol in toRemove)
        {
            var item = currentActive.First(w => w.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            await watchlist.DeleteAsync(accountId, item.Id); // sets IsActive = false

            await history.AddAsync(new WatchlistHistory
            {
                AccountId = accountId,
                Symbol = symbol,
                CompanyName = item.CompanyName,
                Action = WatchlistAction.Removed,
                Reason = "Weekly refresh — not selected",
                WeekStarting = weekStarting,
            });
        }

        // Step 5 — additions
        foreach (var symbol in toAdd)
        {
            var selection = selections.First(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            var existing = await watchlist.GetBySymbolAsync(accountId, symbol);

            if (existing is not null)
            {
                // reactivate previously removed item
                existing.IsActive = true;
                existing.CompanyName = selection.CompanyName;
                existing.Sector = selection.Sector;
                await watchlist.UpdateAsync(existing);
            }
            else
            {
                await watchlist.AddAsync(new WatchlistItem
                {
                    AccountId = accountId,
                    Symbol = symbol,
                    CompanyName = selection.CompanyName,
                    Sector = selection.Sector,
                    IsActive = true,
                });
            }

            await history.AddAsync(new WatchlistHistory
            {
                AccountId = accountId,
                Symbol = symbol,
                CompanyName = selection.CompanyName,
                Action = WatchlistAction.Added,
                Reason = selection.Reason,
                WeekStarting = weekStarting,
            });
        }

        // Step 6 — summary
        logger.LogInformation(
            "Watchlist updated for account {AccountId}: {Added} added, {Removed} removed, {Kept} unchanged",
            accountId, toAdd.Count, toRemove.Count, kept);

        var allActive = await watchlist.GetActiveAsync(accountId);
        logger.LogInformation("New watchlist for account {AccountId}: {Symbols}",
            accountId, string.Join(", ", allActive.Select(w => w.Symbol)));
    }

    private static DateOnly NextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return daysUntilMonday == 0 ? today.AddDays(7) : today.AddDays(daysUntilMonday);
    }
}
