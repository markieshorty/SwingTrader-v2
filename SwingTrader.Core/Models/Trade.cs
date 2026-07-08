using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class Trade : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
    public TradeDirection Direction { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public string? EntryOrderId { get; set; }
    public string? ExitOrderId { get; set; }

    // Null while EntryOrderId/ExitOrderId is set but T212 hasn't confirmed a
    // fill yet - EntryPrice/ExitPrice are set optimistically to the quoted
    // price at order-placement time, which for a market order can differ
    // from the actual fill (slippage). MonitorService polls T212 each cycle
    // for any order still missing its confirmation and overwrites the price
    // with the real fill once available. See MonitorService.ReconcileOrderFillsAsync.
    public DateTime? EntryFillConfirmedAt { get; set; }
    public DateTime? ExitFillConfirmedAt { get; set; }
    public decimal StopLossPrice { get; set; }
    public decimal TargetPrice { get; set; }
    public TradeStatus Status { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal? RealizedPnl { get; set; }
    public int? SignalId { get; set; }
    public decimal? TrailingStopPrice { get; set; }
    public string? Notes { get; set; }

    // Market context at entry/exit — used by regime-aware refinement analysis.
    public decimal? SpyPriceAtEntry { get; set; }
    public decimal? SpyPriceAtExit { get; set; }
    public decimal? VixAtEntry { get; set; }
    public MarketRegime? MarketRegimeAtEntry { get; set; }
    public decimal? SpyReturnDuringTrade { get; set; }

    // Probation phase lifecycle — see MomentumHealthService.
    public TradePhase Phase { get; set; } = TradePhase.Probation;
    public DateTime? PhaseConfirmedAt { get; set; }
    public decimal? MomentumHealthScore { get; set; }
    public string? MomentumHealthVerdict { get; set; }
    public string? MomentumHealthReasoning { get; set; }
    public DateTime? MomentumHealthCheckedAt { get; set; }
}
