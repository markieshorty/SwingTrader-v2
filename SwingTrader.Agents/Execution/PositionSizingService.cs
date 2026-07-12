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
        CapitalTier currentTier,
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

        // Funnel F2: the Forward-score tilt on the per-position BASE budget.
        // Applied before the cash/pool clamps, which stay supreme - the tilt
        // redistributes within the risk budget, never expands it. Exactly 1
        // while the aggressiveness dial is 0 (the default).
        var multiplier = ComputeForwardMultiplier(
            signal.ForwardScore, signal.ForwardScoreDegraded, riskProfile.SizingAggressiveness);

        // Flat mode (SizingMode override): every position is a fixed slice of
        // the whole portfolio - the tier pool and MaxPositionPctOfActive are
        // deliberately bypassed. The locked-capital ceiling is enforced
        // structurally by AccountRiskProfile.Validate() (flat x maxPositions
        // <= un-locked share); the position cap above, the cash buffer, and
        // the daily circuit breaker all still apply.
        if (riskProfile.SizingMode == PositionSizingMode.Flat)
        {
            var flatSpendable = availableCash - totalPortfolioValue * 0.02m;
            if (flatSpendable <= 0)
                return Task.FromResult(new PositionSizeResult(false, 0, 0,
                    "Insufficient cash after applying 2% buffer"));

            var flatBudget = Math.Min(totalPortfolioValue * riskProfile.FlatPositionPct * multiplier, flatSpendable);
            return Task.FromResult(SizeFromBudget(signal, flatBudget, priceOverride, multiplier));
        }

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

        // Step 4 — max position size = active capital * MaxPositionPctOfActive,
        // tilted by the Forward-score multiplier (F2; 1 while the dial is 0).
        var maxPositionBudget = activeCapital * riskProfile.MaxPositionPctOfActive * multiplier;

        // Step 4b — cumulative active-capital cap. The per-position cap alone
        // never bounded TOTAL deployment: at the config extremes (10 positions
        // x 33% of active) the pool could be oversubscribed 3.3x, limited only
        // by cash - defeating the tier system's whole purpose of ramping
        // exposure with proven performance. The remaining headroom in the pool
        // (active capital minus what's already deployed, including earlier
        // placements this run) now also caps the budget.
        var remainingActiveCapital = activeCapital - openPositionsValue;
        if (remainingActiveCapital <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                $"Active capital pool (£{activeCapital:F2} at {currentTier}) is fully deployed (£{openPositionsValue:F2} in open positions)"));

        // Step 5 — apply 2% cash buffer: never spend more than (available - 2% of total)
        var cashBuffer = totalPortfolioValue * 0.02m;
        var spendableCash = availableCash - cashBuffer;

        if (spendableCash <= 0)
            return Task.FromResult(new PositionSizeResult(false, 0, 0,
                "Insufficient cash after applying 2% buffer"));

        // Step 6 — position budget is the least of: per-position cap, spendable
        // cash, and the active pool's remaining headroom (step 4b)
        var positionBudget = Math.Min(Math.Min(maxPositionBudget, spendableCash), remainingActiveCapital);

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
