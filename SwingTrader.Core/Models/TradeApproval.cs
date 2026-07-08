using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class TradeApproval : BaseEntity
{
    // Which mode Report generated this approval for - without this, a Demo
    // approval created earlier in the day could gate/pass a Live execution
    // run after switching mode (see PortfolioSnapshot.TradingMode).
    public TradingMode TradingMode { get; set; }
    public DateOnly TradeDate { get; set; }
    public bool IsApproved { get; set; } = false;
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedSymbols { get; set; }
    public bool IsExpired { get; set; } = false;
    public string? ApprovedVia { get; set; }
    public string? CandidatesJson { get; set; }
}
