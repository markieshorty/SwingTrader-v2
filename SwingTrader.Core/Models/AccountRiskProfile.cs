using SwingTrader.Core.Constants;
using System.ComponentModel.DataAnnotations;

namespace SwingTrader.Core.Models;

// One row per account (like StrategyWeights' active general row) - the
// account-scoped, per-account-adjustable equivalent of the old static
// CapitalRules constants. Buy/Watch thresholds and stop-loss default stay
// on StrategyWeights (already fully wired end-to-end with its own Settings
// UI and a live "must sum to 1.0" validation alongside the component
// weights) rather than being duplicated here - GET /api/risk-profile
// surfaces them read-only from there for a single unified view, but they're
// edited via PUT /api/strategy-weights, not this entity.
public class AccountRiskProfile : BaseEntity
{
    public decimal LockedCapitalPct { get; set; } = CapitalRules.LockedCapitalPct;
    public decimal MaxPositionPctOfActive { get; set; } = CapitalRules.MaxPositionPctOfActive;
    public int MaxOpenPositions { get; set; } = CapitalRules.MaxOpenPositions;
    public decimal DailyLossCircuitBreakerPct { get; set; } = CapitalRules.DailyLossCircuitBreakerPct;
    public int Tier1UnlockMinTrades { get; set; } = CapitalRules.Tier1UnlockMinTrades;
    public decimal Tier1UnlockMinWinRate { get; set; } = CapitalRules.Tier1UnlockMinWinRate;
    public int Tier2UnlockMinTrades { get; set; } = CapitalRules.Tier2UnlockMinTrades;
    public decimal Tier2UnlockMinWinRate { get; set; } = CapitalRules.Tier2UnlockMinWinRate;

    // Trading behaviour — previously hardcoded in appsettings (MonitorConfig / EarningsConfig)
    public int MaxHoldDays { get; set; } = CapitalRules.DefaultMaxHoldDays;
    public double TrailingActivationPct { get; set; } = CapitalRules.DefaultTrailingActivationPct;
    public double TrailingDistancePct { get; set; } = CapitalRules.DefaultTrailingDistancePct;
    public int EarningsGateDays { get; set; } = CapitalRules.DefaultEarningsGateDays;

    // Probation phase — see MomentumHealthService. A position must clear
    // MomentumHealthThreshold on day MinHoldDays or it's flagged for exit
    // rather than being left to run untested until MaxHoldDays.
    public int MinHoldDays { get; set; } = CapitalRules.DefaultMinHoldDays;
    public decimal MomentumHealthThreshold { get; set; } = CapitalRules.DefaultMomentumHealthThreshold;

    // How many symbols Claude selects for the weekly AI-managed watchlist
    // refresh - previously a fixed WatchlistConfig value shared by every
    // account. See CapitalRules.DefaultTargetWatchlistSize for the tradeoff
    // (more symbols = more Research/Monitor API calls per cycle).
    public int TargetWatchlistSize { get; set; } = CapitalRules.DefaultTargetWatchlistSize;

    // Auto-pause new entries while the market regime classifies Bear/Crisis
    // (see MarketRegimeService - structural bear, not a 200dma touch), and
    // auto-resume when it recovers. Long-only momentum bleeds in downtrends;
    // on by default. Monitor still manages exits while paused.
    public bool AutopauseDuringBear { get; set; } = true;

    public string RiskLabel => LockedCapitalPct switch
    {
        >= 0.80m => "Very Conservative",
        >= 0.70m => "Conservative",
        _ when MaxPositionPctOfActive >= 0.25m => "Moderate-Aggressive",
        _ => "Moderate",
    };

    public void Validate()
    {
        if (LockedCapitalPct < CapitalRules.MinLockedCapitalPct || LockedCapitalPct > CapitalRules.MaxLockedCapitalPct)
            throw new ValidationException(
                $"Locked capital must be {CapitalRules.MinLockedCapitalPct:P0}-{CapitalRules.MaxLockedCapitalPct:P0}");

        if (MaxPositionPctOfActive < CapitalRules.MinMaxPositionPctOfActive || MaxPositionPctOfActive > CapitalRules.MaxMaxPositionPctOfActive)
            throw new ValidationException(
                $"Max position must be {CapitalRules.MinMaxPositionPctOfActive:P0}-{CapitalRules.MaxMaxPositionPctOfActive:P0} of active capital");

        if (MaxOpenPositions < CapitalRules.MinMaxOpenPositions || MaxOpenPositions > CapitalRules.MaxMaxOpenPositions)
            throw new ValidationException(
                $"Max positions must be {CapitalRules.MinMaxOpenPositions}-{CapitalRules.MaxMaxOpenPositions}");

        if (DailyLossCircuitBreakerPct < CapitalRules.MinDailyLossCircuitBreakerPct || DailyLossCircuitBreakerPct > CapitalRules.MaxDailyLossCircuitBreakerPct)
            throw new ValidationException(
                $"Circuit breaker must be {CapitalRules.MinDailyLossCircuitBreakerPct:P0}-{CapitalRules.MaxDailyLossCircuitBreakerPct:P0}");

        if (Tier1UnlockMinTrades < CapitalRules.MinTier1UnlockMinTrades || Tier1UnlockMinTrades > CapitalRules.MaxTier1UnlockMinTrades)
            throw new ValidationException(
                $"Tier 1 min trades must be {CapitalRules.MinTier1UnlockMinTrades}-{CapitalRules.MaxTier1UnlockMinTrades}");

        if (Tier1UnlockMinWinRate < CapitalRules.MinTier1UnlockMinWinRate || Tier1UnlockMinWinRate > CapitalRules.MaxTier1UnlockMinWinRate)
            throw new ValidationException(
                $"Tier 1 min win rate must be {CapitalRules.MinTier1UnlockMinWinRate:P0}-{CapitalRules.MaxTier1UnlockMinWinRate:P0}");

        if (Tier2UnlockMinTrades <= Tier1UnlockMinTrades || Tier2UnlockMinTrades > CapitalRules.MaxTier2UnlockMinTrades)
            throw new ValidationException(
                $"Tier 2 min trades must exceed Tier 1 min trades and be at most {CapitalRules.MaxTier2UnlockMinTrades}");

        if (Tier2UnlockMinWinRate <= Tier1UnlockMinWinRate || Tier2UnlockMinWinRate > CapitalRules.MaxTier2UnlockMinWinRate)
            throw new ValidationException(
                $"Tier 2 min win rate must exceed Tier 1 min win rate and be at most {CapitalRules.MaxTier2UnlockMinWinRate:P0}");

        // Active capital sanity check
        var activePct = 1.0m - LockedCapitalPct;
        if (MaxPositionPctOfActive > activePct)
            throw new ValidationException("Max position exceeds available active capital");

        if (MaxHoldDays < CapitalRules.MinMaxHoldDays || MaxHoldDays > CapitalRules.MaxMaxHoldDays)
            throw new ValidationException(
                $"Max hold days must be {CapitalRules.MinMaxHoldDays}-{CapitalRules.MaxMaxHoldDays}");

        if (TrailingActivationPct < CapitalRules.MinTrailingActivationPct || TrailingActivationPct > CapitalRules.MaxTrailingActivationPct)
            throw new ValidationException(
                $"Trailing activation must be {CapitalRules.MinTrailingActivationPct:P0}-{CapitalRules.MaxTrailingActivationPct:P0}");

        if (TrailingDistancePct < CapitalRules.MinTrailingDistancePct || TrailingDistancePct > CapitalRules.MaxTrailingDistancePct)
            throw new ValidationException(
                $"Trailing distance must be {CapitalRules.MinTrailingDistancePct:P0}-{CapitalRules.MaxTrailingDistancePct:P0}");

        if (EarningsGateDays < CapitalRules.MinEarningsGateDays || EarningsGateDays > CapitalRules.MaxEarningsGateDays)
            throw new ValidationException(
                $"Earnings gate days must be {CapitalRules.MinEarningsGateDays}-{CapitalRules.MaxEarningsGateDays}");

        if (MinHoldDays < CapitalRules.AbsoluteMinHoldDays)
            throw new ValidationException(
                $"Probation period must be at least {CapitalRules.AbsoluteMinHoldDays} day");

        if (MomentumHealthThreshold < CapitalRules.MinMomentumHealthThreshold || MomentumHealthThreshold > CapitalRules.MaxMomentumHealthThreshold)
            throw new ValidationException(
                $"Momentum health threshold must be {CapitalRules.MinMomentumHealthThreshold:P0}-{CapitalRules.MaxMomentumHealthThreshold:P0}");

        if (TargetWatchlistSize < CapitalRules.MinTargetWatchlistSize || TargetWatchlistSize > CapitalRules.MaxTargetWatchlistSize)
            throw new ValidationException(
                $"Watchlist size must be {CapitalRules.MinTargetWatchlistSize}-{CapitalRules.MaxTargetWatchlistSize} symbols");

        // Cross-field: a position needs at least one day in the Confirmed
        // phase to run after clearing probation.
        if (MinHoldDays >= MaxHoldDays)
            throw new ValidationException(
                $"Probation period ({MinHoldDays}d) must be less than maximum hold period ({MaxHoldDays}d). " +
                "A position needs time to run after it passes probation.");
    }
}
