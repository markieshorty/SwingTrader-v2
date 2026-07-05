namespace SwingTrader.Agents.Watchlist;

public record ScreenedCandidate(
    string Symbol,
    string CompanyName,
    decimal LastPrice,
    decimal ChangePercent,
    decimal Volume,
    string PrimaryExchange,
    bool IsTopMover = false
);
