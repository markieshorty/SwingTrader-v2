namespace SwingTrader.Agents.Watchlist;

public record WatchlistSelection(
    string Symbol,
    string CompanyName,
    string Sector,
    string Reason
);
