using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Execution;

public class PositionSizingService : IPositionSizingService
{
    public Task<PositionSizeResult> CalculateAsync(
        StockSignal signal,
        CapitalTier currentTier,
        int currentOpenPositions,
        decimal availableCash,
        decimal totalPortfolioValue,
        AccountRiskProfile riskProfile,
        decimal? priceOverride = null)
    {
        // Step 1 — hard cap on concurrent positions
        if (currentOpenPositions >= riskProfile.MaxOpenPositions)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                $"Max open positions ({riskProfile.MaxOpenPositions}) already reached"));

        // Step 2 — determine active capital % for the current tier (cumulative)
        var activeCapitalPct = currentTier switch
        {
            CapitalTier.Tier1 => CapitalRules.Tier1CapitalPct,
            CapitalTier.Tier2 => CapitalRules.Tier2CapitalPct,
            CapitalTier.Tier3 => CapitalRules.Tier3CapitalPct,
            _ => CapitalRules.Tier1CapitalPct
        };

        // Step 3 — active capital pool = total portfolio value * active %
        var activeCapital = totalPortfolioValue * activeCapitalPct;

        // Step 4 — max position size = active capital * MaxPositionPctOfActive
        var maxPositionBudget = activeCapital * riskProfile.MaxPositionPctOfActive;

        // Step 5 — apply 2% cash buffer: never spend more than (available - 2% of total)
        var cashBuffer = totalPortfolioValue * 0.02m;
        var spendableCash = availableCash - cashBuffer;

        if (spendableCash <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                "Insufficient cash after applying 2% buffer"));

        // Step 6 — position budget is the lesser of max budget and spendable cash
        var positionBudget = Math.Min(maxPositionBudget, spendableCash);

        // Use the caller-supplied (GBP-converted) price when given so the budget
        // (GBP) and price are the same currency — otherwise the quantity would be
        // wrong by roughly the FX rate. Falls back to the signal's USD price.
        var price = priceOverride ?? signal.CurrentPrice;
        if (price <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                "Signal price is zero or negative"));

        // Step 7 — compute fractional quantity (3 decimal places), verify ≥ 1 share cost
        var rawQuantity = positionBudget / price;
        var quantity = Math.Floor(rawQuantity * 1000m) / 1000m; // truncate to 3dp

        if (quantity <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                $"Position budget ${positionBudget:F2} is insufficient for one share at ${price:F2}"));

        var estimatedCost = quantity * price;

        return Task.FromResult(new PositionSizeResult(true, quantity, estimatedCost, null));
    }
}
