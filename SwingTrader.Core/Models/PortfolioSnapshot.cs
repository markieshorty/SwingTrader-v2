using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class PortfolioSnapshot : BaseEntity
{
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
