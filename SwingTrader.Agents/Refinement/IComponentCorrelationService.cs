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
    CorrelationAnalysisResult Analyse(
        IReadOnlyList<(StockSignal Signal, bool IsWinner)> scoredTrades,
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
