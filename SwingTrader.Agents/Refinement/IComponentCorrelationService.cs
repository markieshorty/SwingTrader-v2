using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Refinement;

public record CorrelationAnalysisResult(
    List<ComponentFinding> Findings,
    StrategyWeights SuggestedWeights);

public record RegimeCorrelationResult(
    Dictionary<MarketRegime, RegimeAnalysis> RegimeBreakdown,
    Dictionary<MarketRegime, StrategyWeights> SuggestedRegimeWeights,
    decimal MarketAdjustedWinRate,
    bool UnusualMarketConditions,
    string? MarketConditionWarning);

public interface IComponentCorrelationService
{
    // ReturnPct is the trade's market-adjusted return (% vs SPY over the hold,
    // falling back to raw P&L% for trades that predate SPY-return capture) -
    // correlating against it rather than a binary win/loss means a component
    // that predicts frequent small wins but rare large losses scores badly,
    // as it should.
    CorrelationAnalysisResult Analyse(
        IReadOnlyList<(StockSignal Signal, decimal ReturnPct)> scoredTrades,
        StrategyWeights currentWeights,
        decimal maxAdjustmentPerCycle);

    // Splits the same correlation analysis by MarketRegimeAtEntry and separately computes
    // market-adjusted (vs SPY) win rate and a market-condition warning.
    RegimeCorrelationResult AnalyseByRegime(
        IReadOnlyList<(Trade Trade, StockSignal Signal, bool IsWinner)> scoredTrades,
        StrategyWeights currentWeights,
        decimal maxAdjustmentPerCycle,
        int minRegimeSampleSize,
        int analysisPeriodDays);
}
