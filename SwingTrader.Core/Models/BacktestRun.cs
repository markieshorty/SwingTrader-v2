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
// production weights change while the job runs.
public record HistoricBacktestCandidate(
    string Label, HistoricBacktestWeights Weights, decimal BuyThreshold, bool ExcludeBreakout);

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
    List<HistoricBacktestCandidate>? Candidates = null);
