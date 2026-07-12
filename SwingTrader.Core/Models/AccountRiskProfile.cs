using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
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

    // Flat stop-loss / take-profit (fractions: 0.07 = 7% below entry, 0.10 =
    // +10% above). These REPLACED EntryLevelCalculator's per-setup and
    // per-conviction tables (2026-07-12); the Lab's rules panel reads them as
    // its defaults, keeping backtest and live in lockstep.
    public decimal StopLossPct { get; set; } = CapitalRules.DefaultStopLossPct;
    public decimal TargetPct { get; set; } = CapitalRules.DefaultTargetPct;

    // Position sizing mode. TierLadder (default) budgets from the earned
    // tier's active-capital pool; Flat budgets every position as
    // FlatPositionPct of the whole portfolio - the deliberate "I understand
    // the training wheels and I'm choosing to take them off" override. Tier
    // evaluation and readiness keep running either way; Flat only stops the
    // tier from GATING size. Locked capital remains a hard ceiling in both
    // modes (see Validate()).
    public PositionSizingMode SizingMode { get; set; } = PositionSizingMode.TierLadder;
    public decimal FlatPositionPct { get; set; } = CapitalRules.DefaultFlatPositionPct;

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

    // Funnel Phase F2 (docs/funnel-plan): how strongly the Forward score
    // tilts position size. 0 (default) = every position gets base size -
    // the multiplier is exactly 1 and this dial changes nothing. 1 = sizes
    // span 0.5x-1.5x of the per-position base by Forward score. Distinct
    // from the reverted conviction-weighted sizing (see
    // PositionSizingService's NOTE): this tilts on the FORWARD score, whose
    // predictiveness the scorecard must earn before this dial is raised.
    public decimal SizingAggressiveness { get; set; } = 0m;

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

        if (StopLossPct < CapitalRules.MinStopLossPct || StopLossPct > CapitalRules.MaxStopLossPct)
            throw new ValidationException(
                $"Stop-loss must be {CapitalRules.MinStopLossPct:P0}-{CapitalRules.MaxStopLossPct:P0}");

        if (TargetPct < CapitalRules.MinTargetPct || TargetPct > CapitalRules.MaxTargetPct)
            throw new ValidationException(
                $"Target must be {CapitalRules.MinTargetPct:P0}-{CapitalRules.MaxTargetPct:P0}");

        if (TargetPct <= StopLossPct)
            throw new ValidationException("Target must exceed the stop-loss (risk/reward below 1 is a losing structure)");

        if (FlatPositionPct < CapitalRules.MinFlatPositionPct || FlatPositionPct > CapitalRules.MaxFlatPositionPct)
            throw new ValidationException(
                $"Flat position size must be {CapitalRules.MinFlatPositionPct:P0}-{CapitalRules.MaxFlatPositionPct:P0}");

        // Flat mode bypasses the tier pool but NEVER the locked-capital
        // ceiling: full deployment must fit inside the un-locked share.
        if (SizingMode == PositionSizingMode.Flat && FlatPositionPct * MaxOpenPositions > activePct)
            throw new ValidationException(
                $"Flat sizing ({FlatPositionPct:P0} × {MaxOpenPositions} positions) exceeds the un-locked share of the account ({activePct:P0}) — lower the flat size, position count, or locked capital");

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

        if (SizingAggressiveness < CapitalRules.MinSizingAggressiveness || SizingAggressiveness > CapitalRules.MaxSizingAggressiveness)
            throw new ValidationException(
                $"Sizing aggressiveness must be {CapitalRules.MinSizingAggressiveness:0.0}-{CapitalRules.MaxSizingAggressiveness:0.0}");

        // Cross-field: a position needs at least one day in the Confirmed
        // phase to run after clearing probation.
        if (MinHoldDays >= MaxHoldDays)
            throw new ValidationException(
                $"Probation period ({MinHoldDays}d) must be less than maximum hold period ({MaxHoldDays}d). " +
                "A position needs time to run after it passes probation.");
    }
}
