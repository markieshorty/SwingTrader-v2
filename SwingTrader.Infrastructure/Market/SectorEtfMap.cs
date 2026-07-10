namespace SwingTrader.Infrastructure.Market;

// Maps a stock to the sector ETF its relative strength is judged against.
// Resolution order:
//   1. Symbol override (the original hand-picked map - notably keeps the
//      semiconductor names on SMH, a tighter benchmark than their GICS
//      sector's XLK, and preserves scoring continuity for these 22 names).
//   2. GICS sector name (from the Wikipedia constituent tables via
//      IMarketUniverseService) -> the matching SPDR sector ETF.
//   3. SPY - market-relative fallback for anything unmapped (Nasdaq-only
//      additions without an S&P row, sector text drift, missing data).
public static class SectorEtfMap
{
    private static readonly Dictionary<string, string> _symbolOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // Technology
        ["AAPL"]  = "XLK",
        ["MSFT"]  = "XLK",
        ["GOOGL"] = "XLK",
        ["NVDA"]  = "SMH",
        ["AMD"]   = "SMH",
        ["LRCX"]  = "SMH",
        ["AMAT"]  = "SMH",
        // Healthcare
        ["JNJ"]   = "XLV",
        ["UNH"]   = "XLV",
        ["PFE"]   = "XLV",
        ["ABBV"]  = "XLV",
        ["MRK"]   = "XLV",
        // Finance
        ["JPM"]   = "XLF",
        ["BAC"]   = "XLF",
        ["GS"]    = "XLF",
        ["MS"]    = "XLF",
        ["V"]     = "XLF",
        // Consumer discretionary
        ["AMZN"]  = "XLY",
        ["TSLA"]  = "XLY",
        ["HD"]    = "XLY",
        // Consumer staples
        ["WMT"]   = "XLP",
        ["MCD"]   = "XLP",
    };

    // GICS sector names exactly as the Wikipedia S&P constituent tables
    // render them -> SPDR Select Sector ETFs (all 11).
    private static readonly Dictionary<string, string> _sectorEtfs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Information Technology"]   = "XLK",
        ["Health Care"]              = "XLV",
        ["Financials"]               = "XLF",
        ["Consumer Discretionary"]   = "XLY",
        ["Consumer Staples"]         = "XLP",
        ["Energy"]                   = "XLE",
        ["Industrials"]              = "XLI",
        ["Materials"]                = "XLB",
        ["Utilities"]                = "XLU",
        ["Real Estate"]              = "XLRE",
        ["Communication Services"]   = "XLC",
    };

    public static string Resolve(string symbol, string? gicsSector) =>
        _symbolOverrides.TryGetValue(symbol, out var overrideEtf) ? overrideEtf
        : gicsSector is not null && _sectorEtfs.TryGetValue(gicsSector.Trim(), out var sectorEtf) ? sectorEtf
        : "SPY";

    // Legacy overload: override map or SPY. Used where no sector source
    // exists (the offline console backtester's CSV data set).
    public static string GetEtf(string symbol) => Resolve(symbol, null);

    // Every ETF the map can produce - the candle sync must store bars for all
    // of them so the backtester's RS component has its benchmarks.
    public static IReadOnlyCollection<string> AllEtfs() =>
        _symbolOverrides.Values.Concat(_sectorEtfs.Values).Distinct().ToList();
}
