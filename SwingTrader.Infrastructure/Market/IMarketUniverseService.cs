namespace SwingTrader.Infrastructure.Market;

public interface IMarketUniverseService
{
    /// <summary>
    /// Returns the current screening universe: deduplicated union of S&amp;P
    /// 500 and Nasdaq-100 constituents (via Wikipedia). Cached for
    /// WatchlistConfig.UniverseCacheDays. Empty list if both fetches
    /// failed - callers must treat that as "abort, don't proceed with zero
    /// symbols" rather than silently scanning nothing.
    /// </summary>
    Task<List<string>> GetUniverseAsync(CancellationToken ct = default);

    /// <summary>
    /// Same universe as <see cref="GetUniverseAsync"/> but with each ticker's
    /// company name, for display (the /watchlists Stock List Universe tab).
    /// Shares the same cache.
    /// </summary>
    Task<List<UniverseSymbol>> GetUniverseWithNamesAsync(CancellationToken ct = default);
}
