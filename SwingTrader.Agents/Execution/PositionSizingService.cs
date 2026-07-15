using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Execution;

public class PositionSizingService : IPositionSizingService
{
    // NOTE: conviction-weighted sizing (0.5x-1.0x over conviction 6-9) was
    // added 2026-07-10 and reverted the same day after backtesting: over Oct
    // 2023 - Jul 2026 it nearly halved total return (+14.1% -> +7.6% on
    // identical trades) because the conviction 7-8 bucket - the trades it
    // upsized - averaged -0.86%/trade while the 6-7 bucket it shrank averaged
    // +0.42%. With current weights, conviction is not predictive above ~7, so
    // sizing on it puts more money on worse trades. Revisit only once the
    // refinement loop makes conviction genuinely predictive at the top end.
    //
    // The FORWARD-score tilt below (funnel Phase F2) is deliberately not a
    // repeat of that mistake: it tilts on a different signal (the
    // forward-looking pair, not the technical blend), ships inert
    // (aggressiveness defaults 0 = multiplier exactly 1), and the dial is
    // only to be raised once the scorecard shows ForwardScoreAtEntry
    // actually correlates with outcomes.

    // Pure so the maths is directly testable: 1 + aggressiveness * MaxTilt *
    // (forward-5)/5, clamped to the tilt band. Missing or degraded forward
    // scores always yield exactly 1 - unproven-data outages must never move
    // position sizes.
    internal static decimal ComputeForwardMultiplier(
        decimal? forwardScore, bool forwardDegraded, decimal aggressiveness)
    {
        if (forwardScore is not { } forward || forwardDegraded || aggressiveness <= 0m)
            return 1m;

        var tilt = Math.Clamp((forward - 5m) / 5m, -1m, 1m);
        return 1m + Math.Clamp(aggressiveness, 0m, CapitalRules.MaxSizingAggressiveness)
                  * CapitalRules.MaxSizingTilt * tilt;
    }

    public Task<PositionSizeResult> CalculateAsync(
        StockSignal signal,
        int currentOpenPositions,
        decimal availableCash,
        decimal totalPortfolioValue,
        AccountRiskProfile riskProfile,
        decimal? priceOverride = null,
        decimal openPositionsValue = 0m)
    {
        // Step 1 — hard cap on concurrent positions
        if (currentOpenPositions >= riskProfile.MaxOpenPositions)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                $"Max open positions ({riskProfile.MaxOpenPositions}) already reached"));

        // Funnel tilt on the per-position base. Only Funnel mode tilts; Flat
        // sizes every position equally. Exactly 1 when the aggressiveness dial
        // is 0 or the forward score is missing/degraded. Applied before the
        // clamps below, which stay supreme - the tilt redistributes within the
        // risk budget, never expands it.
        var multiplier = riskProfile.SizingMode == PositionSizingMode.Funnel
            ? ComputeForwardMultiplier(signal.ForwardScore, signal.ForwardScoreDegraded, riskProfile.SizingAggressiveness)
            : 1m;

        // Base per-position budget: a flat slice of the whole portfolio. The
        // locked-capital ceiling is enforced structurally by
        // AccountRiskProfile.Validate() (FlatPositionPct x maxPositions <=
        // un-locked share). The cumulative un-locked cap, cash buffer, position
        // cap above and the daily circuit breaker all still apply.
        var baseBudget = totalPortfolioValue * riskProfile.FlatPositionPct * multiplier;

        // Cumulative deployment cap: never let total deployed exceed the
        // un-locked (deployable) share - the reserve behind LockedCapitalPct
        // stays untouched even across the day's placements.
        var deployable = totalPortfolioValue * (1m - riskProfile.LockedCapitalPct);
        var remainingDeployable = deployable - openPositionsValue;
        if (remainingDeployable <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                $"Deployable capital (£{deployable:F2}) is fully committed (£{openPositionsValue:F2} in open positions)"));

        // 2% cash buffer: never spend more than (available - 2% of total)
        var spendableCash = availableCash - totalPortfolioValue * 0.02m;
        if (spendableCash <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                "Insufficient cash after applying 2% buffer"));

        var positionBudget = Math.Min(Math.Min(baseBudget, spendableCash), remainingDeployable);
        return Task.FromResult(SizeFromBudget(signal, positionBudget, priceOverride, multiplier));
    }

    // Budget -> fractional quantity, shared by both sizing modes.
    private static PositionSizeResult SizeFromBudget(
        StockSignal signal, decimal positionBudget, decimal? priceOverride, decimal appliedMultiplier)
    {
        // Use the caller-supplied (GBP-converted) price when given so the budget
        // (GBP) and price are the same currency — otherwise the quantity would be
        // wrong by roughly the FX rate. Falls back to the signal's USD price.
        var price = priceOverride ?? signal.CurrentPrice;
        if (price <= 0)
            return new PositionSizeResult(false, 0, 0, "Signal price is zero or negative");

        // Fractional quantity (3 decimal places), verify ≥ 1 share cost
        var rawQuantity = positionBudget / price;
        var quantity = Math.Floor(rawQuantity * 1000m) / 1000m; // truncate to 3dp

        if (quantity <= 0)
            return new PositionSizeResult(false, 0, 0,
                $"Position budget ${positionBudget:F2} is insufficient for one share at ${price:F2}");

        return new PositionSizeResult(true, quantity, quantity * price, null, appliedMultiplier);
    }
}
