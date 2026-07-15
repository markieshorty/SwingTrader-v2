using System.ComponentModel.DataAnnotations;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// Per-setup entry/exit tactics (docs/setup-tactics-plan). The regime risk book
// is the exposure ENVELOPE (how much, how many, whether to trade); this is how
// an individual trade is MANAGED once taken, keyed on the setup that triggered
// it. One row per (AccountId, SetupType). Orthogonal to regime - the same setup
// is managed the same way in any regime (regime modifiers are a later phase).
//
// These values are FROZEN onto the Trade at entry (thesis-as-contract), so
// editing them only affects new positions. GuideHoldDays currently freezes as
// the hard time-exit (Trade.MaxHoldDaysAtEntry); the momentum-gated "runner"
// extension is Phase 3.
public class SetupTactics : BaseEntity
{
    public SetupType SetupType { get; set; }

    // Initial stop below entry (0.05 = 5%) and profit target above it.
    public decimal StopLossPct { get; set; } = CapitalRules.DefaultStopLossPct;
    public decimal TargetPct { get; set; } = CapitalRules.DefaultTargetPct;

    // Soft time horizon for this setup. A snap-back wants a short guide; a trend
    // runner a long one. (Hard time-exit for now; runner extension is Phase 3.)
    public int GuideHoldDays { get; set; } = CapitalRules.DefaultMaxHoldDays;

    // Trailing-stop shape: how far in profit before the trail arms, and how far
    // below the peak it sits.
    public double TrailingActivationPct { get; set; } = CapitalRules.DefaultTrailingActivationPct;
    public double TrailingDistancePct { get; set; } = CapitalRules.DefaultTrailingDistancePct;

    public void Validate()
    {
        if (StopLossPct < CapitalRules.MinStopLossPct || StopLossPct > CapitalRules.MaxStopLossPct)
            throw new ValidationException(
                $"Stop-loss must be {CapitalRules.MinStopLossPct:P0}-{CapitalRules.MaxStopLossPct:P0}");

        if (TargetPct < CapitalRules.MinTargetPct || TargetPct > CapitalRules.MaxTargetPct)
            throw new ValidationException(
                $"Target must be {CapitalRules.MinTargetPct:P0}-{CapitalRules.MaxTargetPct:P0}");

        if (TargetPct <= StopLossPct)
            throw new ValidationException("Target must exceed the stop-loss (risk/reward below 1 is a losing structure)");

        if (GuideHoldDays < CapitalRules.MinMaxHoldDays || GuideHoldDays > CapitalRules.MaxMaxHoldDays)
            throw new ValidationException(
                $"Guide-hold days must be {CapitalRules.MinMaxHoldDays}-{CapitalRules.MaxMaxHoldDays}");

        if (TrailingActivationPct < CapitalRules.MinTrailingActivationPct || TrailingActivationPct > CapitalRules.MaxTrailingActivationPct)
            throw new ValidationException(
                $"Trailing activation must be {CapitalRules.MinTrailingActivationPct:P0}-{CapitalRules.MaxTrailingActivationPct:P0}");

        if (TrailingDistancePct < CapitalRules.MinTrailingDistancePct || TrailingDistancePct > CapitalRules.MaxTrailingDistancePct)
            throw new ValidationException(
                $"Trailing distance must be {CapitalRules.MinTrailingDistancePct:P0}-{CapitalRules.MaxTrailingDistancePct:P0}");
    }
}
