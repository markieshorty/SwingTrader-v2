using System.Text.Json.Serialization;

namespace SwingTrader.Core.Models;

public class WatchlistItem : BaseEntity
{
    public int WatchlistId { get; set; }

    // Back-reference for EF navigation only - excluded from JSON responses.
    // Without this, GET /api/watchlists (Watchlist -> Items -> WatchlistItem
    // -> Watchlist -> Items -> ...) throws a JsonException ("possible object
    // cycle") since System.Text.Json doesn't handle reference cycles by
    // default, which surfaced as a 500 on every /api/watchlists* endpoint.
    [JsonIgnore]
    public Watchlist? Watchlist { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? Notes { get; set; }

    // When true, this item is researched every trading day regardless of
    // whether its parent Watchlist is enabled, and is excluded from the
    // stock screener's candidate pool the same way any active watchlist item
    // is - so a pick on a disabled/manual watchlist can still be forced
    // straight into the research pipeline without enabling the whole list.
    public bool ForceIntoFinalList { get; set; }
}
