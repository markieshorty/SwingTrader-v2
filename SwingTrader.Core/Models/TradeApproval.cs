namespace SwingTrader.Core.Models;

public class TradeApproval : BaseEntity
{
    public DateOnly TradeDate { get; set; }
    public string ApprovalToken { get; set; } = string.Empty;
    public bool IsApproved { get; set; } = false;
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedSymbols { get; set; }
    public bool IsExpired { get; set; } = false;
    public string? ApprovedVia { get; set; }
}
