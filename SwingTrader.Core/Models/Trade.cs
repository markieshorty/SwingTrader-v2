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
}
