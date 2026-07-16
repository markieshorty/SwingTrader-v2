namespace SwingTrader.Agents.Watchlist;

public record WatchlistUpdateResult(int Added, int Removed, int Kept);

public interface IWatchlistUpdateService
{
    Task<WatchlistUpdateResult> UpdateAsync(int accountId, List<WatchlistSelection> selections, CancellationToken ct = default);
}
