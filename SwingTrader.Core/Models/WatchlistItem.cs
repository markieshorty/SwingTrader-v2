namespace SwingTrader.Core.Models;

public class WatchlistItem : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
}
