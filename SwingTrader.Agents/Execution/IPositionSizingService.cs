using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Execution;

public record PositionSizeResult(
    bool CanTrade,
    decimal Quantity,
    decimal EstimatedCost,
    string? RejectionReason
);

public interface IPositionSizingService
{
    /// <summary>
    /// Calculates position size. <paramref name="priceOverride"/>, when supplied,
    /// is used in place of <c>signal.CurrentPrice</c> for the quantity/cost math —
    /// the caller passes the GBP-converted price so EstimatedCost comes back in the
    /// account's base currency (GBP), directly comparable to available GBP cash.
    /// <paramref name="openPositionsValue"/> is the GBP value already deployed in
    /// open positions (including earlier placements in the same run) — the tier's
    /// active-capital pool is enforced cumulatively against it, not just per
    /// position.
    /// </summary>
    Task<PositionSizeResult> CalculateAsync(
        StockSignal signal,
        CapitalTier currentTier,
        int currentOpenPositions,
        decimal availableCash,
        decimal totalPortfolioValue,
        AccountRiskProfile riskProfile,
        decimal? priceOverride = null,
        decimal openPositionsValue = 0m);
}
