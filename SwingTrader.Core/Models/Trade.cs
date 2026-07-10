using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class Trade : BaseEntity
{
    // Which T212 endpoint (Demo vs Live) this trade was placed under - Demo
    // and Live trades for the same account are financially unrelated, so
    // mixing them in win-rate/tier/refinement/readiness stats or the
    // same-day re-buy guard would produce wrong results (see
    // PortfolioSnapshot.TradingMode for the original instance of this bug).
    public TradingMode TradingMode { get; set; }
    public string Symbol { get; set; } = string.Empty;
    // Copied from the StockSignal this trade was placed from (see
    // StockSignal.CompanyName) - null for trades placed before this field
    // existed.
    public string? CompanyName { get; set; }
    public TradeDirection Direction { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public string? EntryOrderId { get; set; }
    public string? ExitOrderId { get; set; }

    // The exact T212 instrument ticker this trade was placed against, e.g.
    // "AAPL_US_EQ" or "HAL1a_EQ". Symbol resolution isn't reversible - T212
    // inserts listing disambiguators ("HAL1a") and some instruments resolve by
    // Name rather than ticker prefix, so the ticker can't be reconstructed
    // from Symbol alone. Position-drift reconciliation matches broker holdings
    // against this stored ticker exactly. Null for trades placed before this
    // field existed (reconciliation falls back to symbol-prefix heuristics).
    public string? BrokerTicker { get; set; }

    // Null while EntryOrderId/ExitOrderId is set but T212 hasn't confirmed a
    // fill yet - EntryPrice/ExitPrice are set optimistically to the quoted
    // price at order-placement time, which for a market order can differ
    // from the actual fill (slippage). MonitorService polls T212 each cycle
    // for any order still missing its confirmation and overwrites the price
    // with the real fill once available. See MonitorService.ReconcileOrderFillsAsync.
    public DateTime? EntryFillConfirmedAt { get; set; }
    public DateTime? ExitFillConfirmedAt { get; set; }

    // The real £ cash flow for this leg (T212's fill.walletImpact.netValue),
    // captured alongside EntryFillConfirmedAt/ExitFillConfirmedAt above -
    // EntryPrice/ExitPrice are always the per-share price in the instrument's
    // own currency (USD for US-listed stocks), which isn't what the account
    // actually spent/received once FX conversion is applied. Null until the
    // corresponding fill is confirmed.
    public decimal? EntryValueGbp { get; set; }
    public decimal? ExitValueGbp { get; set; }

    // T212's actual currency-conversion fee for this leg (£, positive =
    // cost), from fill.walletImpact.taxes - captured separately from
    // EntryValueGbp/ExitValueGbp since inferring fees by subtracting Real
    // Money Exit from Real Money Entry would conflate the fee with FX-rate
    // drift between the two legs, which are two different things.
    public decimal? EntryFeesGbp { get; set; }
    public decimal? ExitFeesGbp { get; set; }
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
