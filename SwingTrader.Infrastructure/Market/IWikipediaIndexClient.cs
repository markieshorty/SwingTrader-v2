namespace SwingTrader.Infrastructure.Market;

// Free, no-API-key alternative to Finnhub's /index/constituents (which
// requires a paid plan tier - see MarketUniverseService). Wikipedia's
// community-maintained S&P 500/Nasdaq-100 lists are a well-established,
// commonly used data source for exactly this purpose.
public interface IWikipediaIndexClient
{
    Task<List<string>> GetSp500ConstituentsAsync(CancellationToken ct = default);
    Task<List<string>> GetNasdaq100ConstituentsAsync(CancellationToken ct = default);
}
