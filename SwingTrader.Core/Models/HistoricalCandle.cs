namespace SwingTrader.Core.Models;

// Shared platform-level daily bar (adjusted OHLCV from Tiingo) - deliberately
// NOT account-scoped: historical market data is identical for every user, so
// one copy serves all accounts' historic backtests. Synced by CandleSync
// using the platform Tiingo key (Tiingo:PlatformApiKey), never per-user keys
// (free-tier Tiingo caps unique symbols per month far below the universe).
public class HistoricalCandle
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    // Adjusted values - split/dividend-consistent history, matching what the
    // local backtester's CSVs store.
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
}
