using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Watchlist;

public interface IWatchlistSelectionService
{
    Task<List<WatchlistSelection>?> SelectAsync(
        IClaudeClient claude,
        List<ScreenedCandidate> candidates,
        decimal spyChangePercent,
        decimal vix,
        CancellationToken ct = default);
}
