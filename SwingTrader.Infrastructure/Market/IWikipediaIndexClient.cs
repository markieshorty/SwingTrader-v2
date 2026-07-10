namespace SwingTrader.Infrastructure.Market;

// One constituent: ticker, company name, and GICS sector (the S&P 500/400/600
// tables all carry a "GICS Sector" column - verified 10 Jul 2026; the
// Nasdaq-100 table may not, so Sector is nullable and unmapped symbols fall
// back to SPY in SectorEtfMap).
public record UniverseSymbol(string Symbol, string CompanyName, string? Sector = null);

// Free, no-API-key alternative to Finnhub's /index/constituents (which
// requires a paid plan tier - see MarketUniverseService). Wikipedia's
// community-maintained S&P 500/Nasdaq-100 lists are a well-established,
// commonly used data source for exactly this purpose.
public interface IWikipediaIndexClient
{
    Task<List<UniverseSymbol>> GetSp500ConstituentsAsync(CancellationToken ct = default);
    Task<List<UniverseSymbol>> GetNasdaq100ConstituentsAsync(CancellationToken ct = default);

    // S&P 400 (MidCap) and S&P 600 (SmallCap) - the other two thirds of the
    // S&P Composite 1500. These are where swing-tradeable 8-12% moves actually
    // live, since mega-caps rarely swing that far. Same Wikipedia table format
    // (a "Symbol"-headed wikitable) as the S&P 500 page.
    Task<List<UniverseSymbol>> GetSp400ConstituentsAsync(CancellationToken ct = default);
    Task<List<UniverseSymbol>> GetSp600ConstituentsAsync(CancellationToken ct = default);
}
