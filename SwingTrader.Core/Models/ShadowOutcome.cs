using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// Where a shadow outcome's signal came from.
public enum ShadowSource
{
    // A signal the live pipeline actually scored (StockSignals row).
    Live = 0,
    // A signal reconstructed by running detection over historical bars. This is
    // the bulk of the population - the live table only covers six weeks.
    Synthetic = 1,
}

// What WOULD have happened to a signal, whether or not it was ever traded
// (docs/scoring-engine-plan SPEC §3).
//
// The live tables hold 2,499 scored signals and 27 outcomes, because only 27
// were filled. Nothing in the new engine - the per-setup calibration, the dial
// sweeps, or the pre-cutover validation gates - can be built on 27 rows. This
// table is the population those depend on.
//
// TWO KINDS OF NUMBER LIVE HERE, and the distinction is the point:
//
//   1. RULE-BASED (Return/ExitReason/...) - the result under one specific set of
//      stop/target/trail/hold dials. Changing a dial changes these, which is why
//      every row records the dial set it ran under and re-running writes new
//      rows rather than overwriting.
//
//   2. RULE-FREE (Fwd*/MaxFavorable/MaxAdverse/HitPlus25...) - properties of the
//      PRICE PATH alone, independent of any exit rule. These survive a dial
//      sweep untouched.
//
// The calibration target is P(+25% in 40 days), which is a rule-free property.
// Deriving it from rule-based returns would be wrong twice over: a 25% target
// caps the winners it is meant to measure, and every dial sweep would silently
// move the calibration set underneath the thing being calibrated.
public class ShadowOutcome : BaseEntity
{
    public ShadowSource Source { get; set; }

    // Set for Source.Live; null for reconstructed history. Deliberately a plain
    // int rather than a navigation property - signals are discarded on engine
    // changes (SPEC §9.4) and a shadow outcome must outlive that.
    public int? SignalId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public DateOnly SignalDate { get; set; }
    public SetupType SetupType { get; set; }

    // Graded membership 0-1 at detection time. Null for rows replayed from live
    // signals produced by the old boolean detector.
    public decimal? Membership { get; set; }

    // ── Provenance ───────────────────────────────────────────────────────────

    // Identifies the dial set this row was replayed under. Rows are only
    // comparable within a version. Required, and part of the uniqueness key.
    public string DialSetVersion { get; set; } = string.Empty;

    // The candle store's dataset version at replay time. A survivorship backfill
    // bumps it, which invalidates prior rows - without this a run mixing pre-
    // and post-backfill outcomes looks consistent and isn't.
    public int DatasetVersion { get; set; }

    public DateTime ReplayedAt { get; set; } = DateTime.UtcNow;

    // ── The dials actually used, denormalised so a row is self-describing ────
    // Percentages until SPEC P4 lands; ATR multiples then get their own
    // DialSetVersion rather than a schema change.

    public decimal StopLossPct { get; set; }
    public decimal TargetPct { get; set; }
    public int GuideHoldDays { get; set; }
    public decimal TrailingActivationPct { get; set; }
    public decimal TrailingDistancePct { get; set; }

    // ── Rule-based outcome ───────────────────────────────────────────────────

    public DateOnly? EntryDate { get; set; }
    public decimal? EntryPrice { get; set; }
    public DateOnly? ExitDate { get; set; }
    public decimal? ExitPrice { get; set; }

    // StopLoss / Target / Trailing / TimeExit / StillOpen
    public string? ExitReason { get; set; }

    // Net of the backtester's 0.25%/side round trip, so it is comparable with
    // Lab results.
    public decimal? ReturnPct { get; set; }
    public int? TradingDaysHeld { get; set; }

    // Ran out of bars: ReturnPct is marked to the last close, not a real exit.
    // Must be excluded from win-rate style statistics.
    public bool StillOpen { get; set; }

    // ── Rule-free path statistics ────────────────────────────────────────────
    // Measured from the entry bar's open over a fixed horizon, ignoring stops,
    // targets and trails entirely. Null when the window ran past the end of the
    // available bars - a partial window would bias every statistic downward.

    public decimal? Fwd5Pct { get; set; }
    public decimal? Fwd20Pct { get; set; }
    public decimal? Fwd40Pct { get; set; }

    // Best and worst the position ever got to within the 40-bar horizon. The
    // right-tail objective lives here.
    public decimal? MaxFavorablePct { get; set; }
    public decimal? MaxAdversePct { get; set; }

    // The objective function's event, and its mirror. Null when the 40-bar
    // window is incomplete.
    public bool? HitPlus25Within40 { get; set; }
    public bool? HitMinus25Within40 { get; set; }

    // Same-window move of the symbol's sector ETF, for the sector-relative
    // factor and Q7 (is a sector-wide dip a better or worse reversion
    // candidate). Null when the sector is unmapped or the ETF lacks bars.
    public decimal? SectorFwd40Pct { get; set; }
    public decimal? SectorMoveAtSignalPct { get; set; }
}
