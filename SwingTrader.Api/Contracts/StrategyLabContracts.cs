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
    bool ExcludeBreakout);

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

public record StrategyLabResponse(
    LabResult Result,
    IReadOnlyList<LabSuggestion> Suggestions,
    IReadOnlyList<LabTradeOutcome> Trades,
    string? Warning);               // e.g. small sample caveat
