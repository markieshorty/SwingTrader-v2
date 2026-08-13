namespace SwingTrader.Agents.Research;

// The dials the graded detector runs on (docs/scoring-engine-plan SPEC P1).
//
// Every one of these was a hardcoded constant in SetupDetector - thirteen of
// them, never tuned, never sweepable. Defaults here reproduce the legacy
// thresholds so the first comparison isolates the CHANGE IN SHAPE (graded
// membership, no first-match-wins) from any change in tuning. Sweeping comes
// after that comparison, not before it, or the two effects are inseparable.
//
// Percentages are fractions (0.02 = 2%), matching the rest of the codebase.
public sealed record SetupDialsV2
{
    // ── OversoldRecovery ─────────────────────────────────────────────────────

    // Membership peaks at the ceiling and fades toward the floor: an RSI of 34
    // is a dip, an RSI of 12 is a falling knife. The legacy detector had the
    // ceiling but NO floor - the guard existed only in ScoreRsi, which meant
    // detection happily produced knives for the scorer to mark down later.
    public decimal OversoldRsiCeiling { get; init; } = 35m;
    public decimal OversoldRsiFloor { get; init; } = 20m;

    // The falling-knife confirmation. recoveryBars = 0 IS the retired
    // "OversoldRecoveryLoose" - it is a dial, not a separate setup (SPEC D6).
    public int OversoldRecoveryBars { get; init; } = 4;
    public decimal OversoldRecoveryMinPct { get; init; } = 0m;

    // How far above the lower Bollinger band price must have come back.
    public decimal OversoldBandReclaimPct { get; init; } = 0m;

    // ── Breakout ─────────────────────────────────────────────────────────────

    public decimal BreakoutBandDistancePct { get; init; } = 0m;
    public decimal BreakoutVolumeFloor { get; init; } = 1.5m;
    public decimal BreakoutVolumeIdeal { get; init; } = 2.5m;

    // What it broke OUT of. The legacy detector had no such notion, so a break
    // from a six-week coil and a break from three flat days scored identically -
    // plausibly why Breakout carries the highest gate score and backtests as the
    // drag. Tightness is the prior window's range as a fraction of its mean.
    public int BreakoutPriorRangeBars { get; init; } = 20;
    public decimal BreakoutTightIdeal { get; init; } = 0.08m;
    public decimal BreakoutTightWorst { get; init; } = 0.30m;

    // ── MomentumContinuation ─────────────────────────────────────────────────

    public decimal MomentumRsiFloor { get; init; } = 50m;
    public decimal MomentumRsiCeiling { get; init; } = 65m;
    public decimal MomentumVolumeFloor { get; init; } = 1.0m;

    // ── VolumeSpike ──────────────────────────────────────────────────────────

    public decimal SpikeVolumeFloor { get; init; } = 2.0m;
    public decimal SpikeVolumeIdeal { get; init; } = 4.0m;
    public decimal SpikeChangeFloorPct { get; init; } = 0.015m;

    // ── Cross-cutting ────────────────────────────────────────────────────────

    // Below this a name is not a candidate at all. Replaces the implicit
    // "whatever the detector happened to return" floor.
    public decimal MinMembership { get; init; } = 0.20m;

    public static SetupDialsV2 Legacy { get; } = new();

    // The retired loose variant, expressed as what it always was: the confirmed
    // setup with its falling-knife guard switched off.
    public static SetupDialsV2 LooseOversold { get; } = new()
    {
        OversoldRecoveryBars = 0,
        OversoldRsiFloor = 0m,
    };
}
