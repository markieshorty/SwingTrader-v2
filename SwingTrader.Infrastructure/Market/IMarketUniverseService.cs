using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

public interface IMarketUniverseService
{
    /// <summary>
    /// Returns the current screening universe: deduplicated union of
    /// configured index constituents. Cached for WatchlistConfig.UniverseCacheDays.
    /// Empty list if every configured index call failed - callers must
    /// treat that as "abort, don't proceed with zero symbols" rather than
    /// silently scanning nothing.
    /// </summary>
    Task<List<string>> GetUniverseAsync(IFinnhubClient finnhub, CancellationToken ct = default);
}
