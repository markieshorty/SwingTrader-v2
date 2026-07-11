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
}

// Shared request shape serialized into BacktestRun.RequestJson by the Strategy
// Lab endpoint and deserialized by BacktestConsumerFunction - lives in Core so
// Api and Functions use the identical type.
public record HistoricBacktestWeights(
    decimal Rsi, decimal Macd, decimal Volume, decimal Sentiment,
    decimal SetupQuality, decimal RelativeStrength, decimal PriceLevel, decimal FundamentalMomentum);

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
    decimal? StopLossPct = null,              // flat stop override; null = production setup table
    decimal? TargetPct = null,                // flat target override; null = production conviction table
    bool? SimulateProbation = null,           // null = true (production always runs probation)
    int? MinHoldDays = null,                  // probation check day (trading days held)
    decimal? MomentumHealthThreshold = null); // probation pass bar, 0..1

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
    HistoricTradingRules? Rules = null);
