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

    // Populated only when approval is required for this account - kept
    // separate from ReportMarkdown so the approve/reject links can be
    // emailed exclusively to recipients with TradeApproval ticked, rather
    // than to everyone who gets the general daily report.
    public string? ApprovalMarkdown { get; set; }
}
