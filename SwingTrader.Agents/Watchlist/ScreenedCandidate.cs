namespace SwingTrader.Agents.Watchlist;

public record ScreenedCandidate(
    string Symbol,
    string CompanyName,
    decimal LastPrice,
    decimal ChangePercent,
    decimal Volume,
    string PrimaryExchange,
    bool IsTopMover = false,
    // Cross-sectional selection percentile vs the rest of that day's screened
    // universe (0-100, see CrossSectionalRanker). Null until stamped.
    decimal? SelectionPercentile = null
);
