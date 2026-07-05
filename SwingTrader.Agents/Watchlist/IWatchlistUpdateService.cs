namespace SwingTrader.Agents.Watchlist;

public interface IWatchlistUpdateService
{
    Task UpdateAsync(int accountId, List<WatchlistSelection> selections, CancellationToken ct = default);
}
