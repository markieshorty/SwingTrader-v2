using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// Daily snapshot of readiness metrics for trajectory tracking — lets the
// dashboard show whether the system is progressing or stalled toward each
// feature's activation criteria. Written once per day (upsert on
// AccountId+TradingMode+SnapshotDate) since ScoredClosedTrades/ObservedWinRate
// are derived from mode-scoped trade history (see PortfolioSnapshot.TradingMode).
public class ReadinessSnapshot : BaseEntity
{
    public TradingMode TradingMode { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public int ScoredClosedTrades { get; set; }
    public decimal ObservedWinRate { get; set; }
    public decimal TradesPerWeekWeighted { get; set; }
    public int RegimeBullCount { get; set; }
    public int RegimeNeutralCount { get; set; }
    public int RegimeBearCount { get; set; }
    public int SystemRunningDays { get; set; }
}
