using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IWatchlistHistoryRepository
{
    Task AddAsync(WatchlistHistory entry);

    // The most recent Added-reason per symbol - surfaces the qualitative
    // list's "[Archetype] rationale" lines in the UI so the review-before-
    // enable decision isn't made blind.
    Task<Dictionary<string, string>> GetLatestReasonsAsync(
        int accountId, IReadOnlyCollection<string> symbols, CancellationToken ct = default);
}
