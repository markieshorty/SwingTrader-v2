namespace SwingTrader.Api.Contracts;

// Strategy Lab: re-simulate the account's OWN closed-trade history under
// user-adjusted dials (weights + thresholds + setup filters), report what
// would have happened, and search nearby configurations for improvements.
// "Own data" mode needs no market-data infrastructure: every signal persists
// its 8 component scores, and closed trades link back to their signal, so any
// weight mix can be re-scored exactly.

public record LabWeights(
    decimal Rsi, decimal Macd, decimal Volume, decimal Sentiment,
    decimal SetupQuality, decimal RelativeStrength, decimal PriceLevel, decimal FundamentalMomentum);

public record StrategyLabRequest(
    // "own" (closed-trade replay) | "historic" (full market backtest - phase 2)
    string DataSource,
    LabWeights Weights,
    decimal BuyThreshold,
    bool ExcludeBreakout,
    // A/B mode: also evaluate the current production dials over the same data
    // so the user sees both side by side. Cheap for own-data (a second
    // in-memory replay); for historic it doubles a multi-minute job, so the
    // UI keeps it opt-in there.
    bool CompareBaseline = false,
    // Historic mode only: skip new entries while SPY is below its 200-day
    // average, approximating the live bear autopause. Unlike sentiment etc.,
    // this IS reconstructable from bars, so it's a legitimate dial to test.
    bool AutopauseDuringBear = true);

public record LabTradeOutcome(
    string Symbol, DateTime OpenedAt, decimal Conviction, string Setup, decimal ReturnPct, bool WouldTake);

public record LabResult(
    int TotalClosedTrades,          // trades with scored signals available to replay
    int TradesKept,                 // would still have been taken under these dials
    int TradesDropped,
    int DroppedWinners,             // winners the dials would have filtered out
    int DroppedLosers,              // losers the dials would have avoided
    decimal ActualAvgReturnPct,     // what actually happened (all replayable trades)
    decimal SimAvgReturnPct,        // avg return of the kept subset
    decimal ActualWinRate,
    decimal SimWinRate,
    decimal ActualTotalPnl,
    decimal SimTotalPnl,
    string Summary);                // plain-English interpretation

public record LabSuggestion(
    string Description,             // e.g. "Raise Buy threshold 6.0 -> 6.5"
    LabWeights Weights,
    decimal BuyThreshold,
    bool ExcludeBreakout,
    decimal SimAvgReturnPct,
    decimal SimWinRate,
    int TradesKept,
    decimal ImprovementPct);        // avg-return improvement vs the user's run

// The production dials evaluated over the same data as the user's run, for
// side-by-side comparison. Weights snapshotted at evaluation time so the
// comparison is labelled with what was actually run.
public record LabBaseline(
    LabWeights Weights,
    decimal BuyThreshold,
    bool ExcludeBreakout,
    LabResult Result);

public record StrategyLabResponse(
    LabResult Result,
    IReadOnlyList<LabSuggestion> Suggestions,
    IReadOnlyList<LabTradeOutcome> Trades,
    string? Warning,                // e.g. small sample caveat
    LabBaseline? Baseline = null);  // present when CompareBaseline was requested

// ── Claude analysis ("Analyse this run") ────────────────────────────────────
// Advisory only: Claude reads a completed run and suggests a next config worth
// TESTING. Nothing is run or applied on its behalf - the user loads the dials
// and clicks Run Simulation themselves.

public record LabAnalyseOwnResult(
    int TotalClosedTrades, int TradesKept, int DroppedWinners, int DroppedLosers,
    decimal ActualAvgReturnPct, decimal SimAvgReturnPct, decimal ActualWinRate, decimal SimWinRate);

public record LabAnalyseRequest(
    string DataSource,              // "own" | "historic"
    LabWeights Weights,             // the config that produced the run
    decimal BuyThreshold,
    bool ExcludeBreakout,
    LabAnalyseOwnResult? OwnResult, // own mode: the result the UI displayed
    int? BacktestRunId,             // historic mode: result loaded server-side
    bool AutopauseDuringBear = true);

public record LabAnalyseSuggestion(
    string Rationale,               // one sentence: what this tests and why
    LabWeights Weights,
    decimal BuyThreshold,
    bool ExcludeBreakout);

public record LabAnalyseResponse(
    string Analysis,                    // plain-text paragraphs
    LabAnalyseSuggestion? Suggestion);  // null when the data doesn't justify one

// ── Apply from the Strategy Lab ─────────────────────────────────────────────
// One-click apply that still leaves a full audit trail: the endpoint records a
// RefinementSuggestion (Origin = StrategyLab) and immediately applies it via
// the same service the refinement page's Approve button uses - so every
// production weight change, whatever tool proposed it, appears in the
// refinement history with its evidence.
public record LabApplyRequest(
    LabWeights Weights,
    decimal BuyThreshold,
    // Plain-English description of the run that justified this change, e.g.
    // "Optimizer sweep winner 'Volume +10pp' — held up out-of-sample
    // (train 0.42%, holdout 0.31% vs baseline 0.12%/trade)".
    string EvidenceSummary,
    int TradeCount,                     // trades in the justifying run (0 if n/a)
    decimal WinRate,                    // win rate of the justifying run (0 if n/a)
    SwingTrader.Core.Enums.RefinementConfidenceLevel Confidence);
