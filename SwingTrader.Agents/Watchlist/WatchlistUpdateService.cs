using Microsoft.Extensions.Logging;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Watchlist;

public class WatchlistUpdateService(
    IWatchlistRepository watchlist,
    IWatchlistHistoryRepository history,
    ITradeRepository trades,
    IAccountRepository accountRepo,
    ILogger<WatchlistUpdateService> logger) : IWatchlistUpdateService
{
    public async Task<WatchlistUpdateResult> UpdateAsync(int accountId, List<WatchlistSelection> selections, CancellationToken ct = default)
    {
        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        var weekStarting = NextMonday();
        var newSymbols = selections.Select(s => s.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 1 — current active list
        var currentActive = (await watchlist.GetActiveAsync(accountId)).ToList();
        var currentSymbols = currentActive.Select(w => w.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 7 — protect open positions from removal
        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId, account.TradingMode))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 3 — diff
        var toRemove = currentSymbols.Except(newSymbols, StringComparer.OrdinalIgnoreCase)
            .Where(s => !openTradeSymbols.Contains(s))
            .ToList();
        var toAddCandidates = selections
            .Select(s => s.Symbol)
            .Where(s => !currentSymbols.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // This service always writes to the account's default watchlist
        // (WatchlistItem.AddAsync falls back to it when WatchlistId == 0) - the
        // union cap only matters when that watchlist is itself enabled, same
        // condition WatchlistRepository.AddSymbolAsync uses. Selections beyond
        // the remaining capacity are skipped rather than applied - the manual
        // UI paths reject the request outright, but a scheduled refresh has no
        // user to show a validation error to, so it takes as many of Claude's
        // top picks (selections is already ranked) as fit and logs the rest.
        var toAdd = toAddCandidates;
        var skippedForCapacity = new List<string>();

        var allWatchlists = await watchlist.GetAllWatchlistsAsync(accountId, ct);
        var defaultWatchlist = allWatchlists.FirstOrDefault(w => w.IsDefault);
        if (defaultWatchlist is { IsEnabled: true })
        {
            var enabledUnion = (await watchlist.GetAllEnabledSymbolsAsync(accountId, ct))
                .Select(i => i.Symbol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            enabledUnion.ExceptWith(toRemove);

            var accepted = new List<string>();
            foreach (var symbol in toAddCandidates)
            {
                if (enabledUnion.Count < WatchlistLimits.MaxTotalEnabledSymbols || enabledUnion.Contains(symbol))
                {
                    enabledUnion.Add(symbol);
                    accepted.Add(symbol);
                }
                else
                {
                    skippedForCapacity.Add(symbol);
                }
            }
            toAdd = accepted;

            if (skippedForCapacity.Count > 0)
            {
                logger.LogWarning(
                    "Weekly refresh for account {AccountId} skipped {Count} selection(s) — would exceed the " +
                    "{Max}-symbol enabled-watchlist cap: {Symbols}",
                    accountId, skippedForCapacity.Count, WatchlistLimits.MaxTotalEnabledSymbols, string.Join(", ", skippedForCapacity));
            }
        }

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

        return new WatchlistUpdateResult(toAdd.Count, toRemove.Count, kept, skippedForCapacity);
    }

    private static DateOnly NextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return daysUntilMonday == 0 ? today.AddDays(7) : today.AddDays(daysUntilMonday);
    }
}
