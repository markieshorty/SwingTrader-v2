namespace SwingTrader.Infrastructure.Market;

public static class SectorEtfMap
{
    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
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

    public static string GetEtf(string symbol)
        => _map.TryGetValue(symbol, out var etf) ? etf : "SPY";

    public static IReadOnlyCollection<string> AllEtfs()
        => _map.Values.Distinct().ToList();
}
