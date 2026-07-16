namespace SwingTrader.Core.Models;

// One historic-market simulation requested from the Strategy Lab. Runs take
// minutes (full engine over ~1M bars), so they execute as a Service Bus job
// (backtest-jobs queue) and the UI polls this row for completion.
public class BacktestRun : BaseEntity
{
    // "Queued" | "Running" | "Completed" | "Failed"
    public string Status { get; set; } = "Queued";
    public string RequestJson { get; set; } = string.Empty;   // StrategyLabRequest
    public string? ResultJson { get; set; }                   // HistoricResult (sans trade log)
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Sweep-only progress: null for every other mode. Set once the candidate
    // list is generated, then CompletedCandidates ticks up as each one
    // finishes so the UI can render a determinate progress bar instead of a
    // fixed "expect 10-20 minutes" spinner.
    public int? TotalCandidates { get; set; }
    public int? CompletedCandidates { get; set; }
}

// Shared request shape serialized into BacktestRun.RequestJson by the Strategy
// Lab endpoint and deserialized by BacktestConsumerFunction - lives in Core so
// Api and Functions use the identical type.
// The six gate weights (sentiment & fundamental momentum drive the live
// Forward score, not the backtestable gate, so they aren't swept here).
public record HistoricBacktestWeights(
    decimal Rsi, decimal Macd, decimal Volume,
    decimal SetupQuality, decimal RelativeStrength, decimal PriceLevel);

// One named dial configuration inside a multi-candidate job (A/B comparison or
// optimizer sweep). Baseline configs are snapshotted into the request at queue
// time so the comparison is labelled with what was actually evaluated, even if
// production weights change while the job runs. AutopauseDuringBear maps to
// the engine's regime filter (skip entries while SPY < 200dma), approximating
// the live bear autopause.
// Experiment-only overrides of the trading RULES (as opposed to the scoring
// dials): setup exclusions beyond Breakout, hold/position caps, and trailing
// stop shape. Null field = use the account's live risk-profile value, so an
// untouched run still simulates exactly what production does. These ride the
// request - nothing here ever touches live settings.
public record HistoricTradingRules(
    List<string>? ExcludedSetups = null,      // SetupType names, e.g. ["Breakout","VolumeSpike"]
    int? MaxHoldDays = null,
    int? MaxOpenPositions = null,
    decimal? TrailingActivationPct = null,    // fraction, e.g. 0.05 = arm at +5%
    decimal? TrailingDistancePct = null,      // fraction, e.g. 0.03 = trail 3% below
    decimal? StopLossPct = null,              // flat fallback stop; per-setup tactics below win when present
    decimal? TargetPct = null,                // flat fallback target; per-setup tactics below win when present
    bool? SimulateProbation = null,           // null = true (production always runs probation)
    int? MinHoldDays = null,                  // probation check day (trading days held)
    decimal? MomentumHealthThreshold = null,  // probation pass bar, 0..1
    decimal? PositionFraction = null,         // flat sizing: fraction of equity per trade (default 0.10)
    decimal? LockedCapitalPct = null,         // reserve fraction; total deployment <= 1 - this (null = the book's)
    decimal? ActiveCapitalPct = null,         // sim-only capital-pool sizing (no live equivalent)
    decimal? MaxPositionPctOfActive = null,   // per-position share of the pool; null = risk profile's
    // Per-setup entry/exit tactics (docs/setup-tactics-plan Phase 4). Null =
    // use the account's live SetupTactics unchanged (an untouched run mirrors
    // live). When the Lab's tactics editor is touched it sends the FULL edited
    // set; each named setup's stop/target/guide-hold/trailing overrides the
    // account default for that setup only.
    List<HistoricSetupTacticsOverride>? SetupTactics = null);

// One setup's entry/exit tactics for a Lab run (mirrors the live SetupTactics
// row). Setup is the SetupType name; the rest are fractions / trading days.
public record HistoricSetupTacticsOverride(
    string Setup,
    decimal StopLossPct, decimal TargetPct, int GuideHoldDays,
    decimal TrailingActivationPct, decimal TrailingDistancePct);

public record HistoricBacktestCandidate(
    string Label, HistoricBacktestWeights Weights, decimal BuyThreshold, bool ExcludeBreakout,
    bool AutopauseDuringBear = true,
    HistoricTradingRules? Rules = null);

// Mode:
//   null / "single" - one config, one result (the original shape)
//   "ab"            - Candidates[0] = the user's dials, Candidates[1] = the
//                     production baseline; both evaluated over the full window
//   "sweep"         - Candidates[0] = production baseline; the consumer
//                     generates perturbation candidates around it, evaluates on
//                     a train window and validates the winner out-of-sample
public record HistoricBacktestRequest(
    HistoricBacktestWeights Weights, decimal BuyThreshold, bool ExcludeBreakout,
    string? Mode = null,
    List<HistoricBacktestCandidate>? Candidates = null,
    bool AutopauseDuringBear = true,
    HistoricTradingRules? Rules = null,
    // Sweep-only: also generate trading-rule candidates (exit/probation/
    // position grids) alongside the weight search. Defaulted so older queued
    // requests keep deserializing.
    bool SearchRules = false,
    // Regime envelope both A/B columns replay under (null/"neutral"|"bull"|
    // "bear"|"crisis" force one book; "mixed" switches per detected day) and the
    // user column's per-regime autopause overrides (key = regime name; absent =
    // inherit the live book). Defaulted so older queued requests deserialize.
    string? RegimeMode = null,
    // The user column's per-regime EXPOSURE overrides (key = regime name; a null
    // field inherits the live book). Under a forced regime only Autopause is
    // used; under Mixed all four exposure levers apply per regime (the "3 forms"
    // editor). Defaulted so older queued requests deserialize.
    Dictionary<string, RegimeExposureOverride>? RegimeOverrides = null);

// A per-regime override of the exposure envelope for the user column of a Lab
// run. Each null field inherits that regime's live risk book, so an untouched
// form changes nothing. Percentages are fractions (0.20 = 20%).
public record RegimeExposureOverride(
    bool? Autopause = null,
    decimal? LockedCapitalPct = null,
    decimal? PositionFraction = null,
    int? MaxOpenPositions = null);
