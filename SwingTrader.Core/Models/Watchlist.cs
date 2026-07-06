using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class Watchlist : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public WatchlistType Type { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public string? Description { get; set; }

    // Supplementary Finnhub top-movers candidate source for the Watchlist
    // Agent's weekly refresh (StockScreener) - only meaningful for the
    // default AiManaged watchlist, since that's the only one the agent
    // screens. Off by default, matching the old global-config default.
    public bool TopMoversEnabled { get; set; } = false;
    public List<WatchlistItem> Items { get; set; } = [];
}
