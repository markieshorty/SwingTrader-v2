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
    // Which market-regime book this row is. An account holds one row per
    // MarketRegime (Bull/Neutral/Bear/Crisis); the live regime detector
    // (MarketRegimeService) selects which book is active each cycle. The
    // Neutral row is the account's baseline (the pre-regime single profile
    // migrated into it). Unique per (AccountId, Regime).
    public MarketRegime Regime { get; set; } = MarketRegime.Neutral;

    public decimal LockedCapitalPct { get; set; } = CapitalRules.LockedCapitalPct;
    public int MaxOpenPositions { get; set; } = CapitalRules.MaxOpenPositions;
    public decimal DailyLossCircuitBreakerPct { get; set; } = CapitalRules.DailyLossCircuitBreakerPct;

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

    // Position sizing. Every position is FlatPositionPct of the whole portfolio;
    // Flat (default) treats them all equally, Funnel tilts by the Forward score
    // (SizingAggressiveness). Locked capital is a hard ceiling in both modes:
    // Validate() requires FlatPositionPct x MaxOpenPositions <= 1 - locked.
    public PositionSizingMode SizingMode { get; set; } = PositionSizingMode.Flat;
    public decimal FlatPositionPct { get; set; } = CapitalRules.DefaultFlatPositionPct;

    // How many symbols Claude selects for the weekly AI-managed watchlist
    // refresh - previously a fixed WatchlistConfig value shared by every
    // account. See CapitalRules.DefaultTargetWatchlistSize for the tradeoff
    // (more symbols = more Research/Monitor API calls per cycle).
    public int TargetWatchlistSize { get; set; } = CapitalRules.DefaultTargetWatchlistSize;

    // Auto-pause NEW entries whenever THIS regime's book is the active one
    // (see MarketRegimeService). Monitor engages the pause on entering a book
    // with this on and auto-resumes on moving to one with it off; open
    // positions are still managed while paused. Seeded on for the defensive
    // books (Bear/Crisis), off for Bull/Neutral - long-only momentum bleeds in
    // downtrends. Replaces the old account-wide AutopauseDuringBear toggle.
    public bool AutopauseTrading { get; set; }

    // Funnel Phase F2 (docs/funnel-plan): how strongly the Forward score
    // tilts position size. 0 (default) = every position gets base size -
    // the multiplier is exactly 1 and this dial changes nothing. 1 = sizes
    // span 0.5x-1.5x of the per-position base by Forward score. Distinct
    // from the reverted conviction-weighted sizing (see
    // PositionSizingService's NOTE): this tilts on the FORWARD score, whose
    // predictiveness the scorecard must earn before this dial is raised.
    public decimal SizingAggressiveness { get; set; } = 0m;

    // Funnel Phase F3 (docs/funnel-plan): a gate-passing Buy whose Forward
    // score sits below this floor is demoted to Watch (asymmetric veto -
    // forward information can block, never create, a Buy). Degraded or
    // missing Forward scores never veto (fail-open, like every other data
    // dependency). 0 disables the veto entirely.
    public decimal ForwardVetoFloor { get; set; } = CapitalRules.DefaultForwardVetoFloor;

    public string RiskLabel => LockedCapitalPct switch
    {
        >= 0.80m => "Very Conservative",
        >= 0.70m => "Conservative",
        _ when FlatPositionPct >= 0.15m => "Moderate-Aggressive",
        _ => "Moderate",
    };

    public void Validate()
    {
        if (LockedCapitalPct < CapitalRules.MinLockedCapitalPct || LockedCapitalPct > CapitalRules.MaxLockedCapitalPct)
            throw new ValidationException(
                $"Locked capital must be {CapitalRules.MinLockedCapitalPct:P0}-{CapitalRules.MaxLockedCapitalPct:P0}");

        if (MaxOpenPositions < CapitalRules.MinMaxOpenPositions || MaxOpenPositions > CapitalRules.MaxMaxOpenPositions)
            throw new ValidationException(
                $"Max positions must be {CapitalRules.MinMaxOpenPositions}-{CapitalRules.MaxMaxOpenPositions}");

        if (DailyLossCircuitBreakerPct < CapitalRules.MinDailyLossCircuitBreakerPct || DailyLossCircuitBreakerPct > CapitalRules.MaxDailyLossCircuitBreakerPct)
            throw new ValidationException(
                $"Circuit breaker must be {CapitalRules.MinDailyLossCircuitBreakerPct:P0}-{CapitalRules.MaxDailyLossCircuitBreakerPct:P0}");

        var activePct = 1.0m - LockedCapitalPct;

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

        // Full deployment must fit inside the un-locked share (both modes size
        // from FlatPositionPct; Funnel only tilts within this budget).
        if (FlatPositionPct * MaxOpenPositions > activePct)
            throw new ValidationException(
                $"Position sizing ({FlatPositionPct:P0} × {MaxOpenPositions} positions) exceeds the un-locked share of the account ({activePct:P0}) — lower the position size, position count, or locked capital");

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

        if (ForwardVetoFloor < CapitalRules.MinForwardVetoFloor || ForwardVetoFloor > CapitalRules.MaxForwardVetoFloor)
            throw new ValidationException(
                $"Forward veto floor must be {CapitalRules.MinForwardVetoFloor:0.0}-{CapitalRules.MaxForwardVetoFloor:0.0}");

        // Cross-field: a position needs at least one day in the Confirmed
        // phase to run after clearing probation.
        if (MinHoldDays >= MaxHoldDays)
            throw new ValidationException(
                $"Probation period ({MinHoldDays}d) must be less than maximum hold period ({MaxHoldDays}d). " +
                "A position needs time to run after it passes probation.");
    }
}
