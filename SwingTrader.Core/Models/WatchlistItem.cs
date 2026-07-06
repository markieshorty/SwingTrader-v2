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
}
