using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class PortfolioSnapshot : BaseEntity
{
    // Which T212 endpoint (Demo vs Live) TotalCapital/CashAvailable came
    // from - the two have entirely unrelated balances, so a snapshot taken
    // under one mode is meaningless as a baseline for the other. Switching
    // TradingMode mid-day previously let the circuit breaker compare a
    // morning Demo-balance snapshot against the afternoon's real Live
    // balance, reading as a ~100% drawdown and triggering incorrectly.
    public TradingMode TradingMode { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public decimal TotalCapital { get; set; }
    public decimal LockedCapital { get; set; }
    public decimal ReserveCapital { get; set; }
    public decimal ActiveCapital { get; set; }
    public decimal CashAvailable { get; set; }
    public decimal OpenPositionsValue { get; set; }
    public decimal TotalPnl { get; set; }
    public CapitalTier CurrentTier { get; set; }
}
