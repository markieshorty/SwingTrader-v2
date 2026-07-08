using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class DailyReport : BaseEntity
{
    // The mode PortfolioValue/DailyPnl below were computed under (see
    // PortfolioSnapshot.TradingMode) - kept even though only one mode is
    // ever active per account at a time, so a mode switch never leaves a
    // report readable as belonging to the wrong balance.
    public TradingMode TradingMode { get; set; }
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
