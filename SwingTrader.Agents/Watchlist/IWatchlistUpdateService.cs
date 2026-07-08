namespace SwingTrader.Agents.Watchlist;

// SkippedForCapacity - selections that would have pushed the enabled-watchlist
// union over WatchlistLimits.MaxTotalEnabledSymbols, so they were left off this
// refresh rather than applied. Empty in the common case.
public record WatchlistUpdateResult(int Added, int Removed, int Kept, List<string> SkippedForCapacity);

public interface IWatchlistUpdateService
{
    Task<WatchlistUpdateResult> UpdateAsync(int accountId, List<WatchlistSelection> selections, CancellationToken ct = default);
}
