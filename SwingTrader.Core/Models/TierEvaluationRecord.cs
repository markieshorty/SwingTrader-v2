using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class TierEvaluationRecord : BaseEntity
{
    public DateTime EvaluatedAt { get; set; }
    public DateOnly EvaluationPeriodStart { get; set; }
    public DateOnly EvaluationPeriodEnd { get; set; }
    public CapitalTier CurrentTier { get; set; }
    public int TotalTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal AvgReturnPct { get; set; }
    public decimal? SharpeRatio { get; set; }
    public decimal MaxDrawdownPct { get; set; }
    public bool UnlockCriteriaMet { get; set; }
    public CapitalTier SuggestedTier { get; set; }
    public CapitalTier ActualTierAfter { get; set; }
    public bool WasApplied { get; set; }
    public string? Notes { get; set; }
}
