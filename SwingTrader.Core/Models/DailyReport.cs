namespace SwingTrader.Core.Models;

public class DailyReport : BaseEntity
{
    public DateOnly ReportDate { get; set; }
    public string ReportMarkdown { get; set; } = string.Empty;
    public string TopBuysJson { get; set; } = string.Empty;
    public string TopSellsJson { get; set; } = string.Empty;
    public string MarketContext { get; set; } = string.Empty;
    public decimal PortfolioValue { get; set; }
    public decimal DailyPnl { get; set; }
    public bool WasSent { get; set; }
}
