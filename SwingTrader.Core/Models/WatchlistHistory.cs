using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class WatchlistHistory : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public WatchlistAction Action { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateOnly WeekStarting { get; set; }
    public decimal? ConvictionAtAdd { get; set; }
    public string? ReplacedSymbol { get; set; }
}
