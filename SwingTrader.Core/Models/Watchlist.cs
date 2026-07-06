using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class Watchlist : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public WatchlistType Type { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public string? Description { get; set; }
    public List<WatchlistItem> Items { get; set; } = [];
}
