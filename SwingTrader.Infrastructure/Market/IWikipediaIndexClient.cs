namespace SwingTrader.Infrastructure.Market;

// Free, no-API-key alternative to Finnhub's /index/constituents (which
// requires a paid plan tier - see MarketUniverseService). Wikipedia's
// community-maintained S&P 500/Nasdaq-100 lists are a well-established,
// commonly used data source for exactly this purpose.
public interface IWikipediaIndexClient
{
    Task<List<string>> GetSp500ConstituentsAsync(CancellationToken ct = default);
    Task<List<string>> GetNasdaq100ConstituentsAsync(CancellationToken ct = default);

    // S&P 400 (MidCap) and S&P 600 (SmallCap) - the other two thirds of the
    // S&P Composite 1500. These are where swing-tradeable 8-12% moves actually
    // live, since mega-caps rarely swing that far. Same Wikipedia table format
    // (a "Symbol"-headed wikitable) as the S&P 500 page.
    Task<List<string>> GetSp400ConstituentsAsync(CancellationToken ct = default);
    Task<List<string>> GetSp600ConstituentsAsync(CancellationToken ct = default);
}
