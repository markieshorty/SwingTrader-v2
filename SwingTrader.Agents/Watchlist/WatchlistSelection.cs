namespace SwingTrader.Agents.Watchlist;

public record WatchlistSelection(
    string Symbol,
    string CompanyName,
    string Sector,
    string Reason,
    // Cross-sectional selection percentile from the screener (0-100), mapped
    // back onto Claude's pick by symbol. Null when the candidate wasn't found.
    decimal? SelectionPercentile = null
);
