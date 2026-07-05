using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Readiness;

public record ReadinessReport(
    DateTime GeneratedAt,
    DataMaturityLevel OverallMaturity,
    int SystemRunningDays,
    int TotalSignalsGenerated,
    int TotalClosedTrades,
    int ScoredClosedTrades,
    WinRateAssessment WinRate,
    TradeRateAssessment TradeRate,
    Dictionary<MarketRegime, int> RegimeTradeCount,
    List<FeatureReadiness> Features,
    List<DataMilestone> UpcomingMilestones,
    List<ReadinessSnapshot> TrajectoryHistory);

public record WinRateAssessment(
    decimal ObservedRate,
    decimal? ConfidenceLow,
    decimal? ConfidenceHigh,
    bool HasSufficientData,
    string DisplayText);

public record TradeRateAssessment(
    decimal WeightedTradesPerWeek,
    decimal HistoricalTradesPerWeek,
    decimal RecentTradesPerWeek,
    bool HasReliableEstimate);

public record ReadinessCriteria(
    string Description,
    bool Met,
    string CurrentValue,
    string RequiredValue,
    string? Context);

public record FeatureReadiness(
    string FeatureName,
    string? ConfigKey,
    string? ConfigValue,
    ReadinessStatus Status,
    string Assessment,
    string? Recommendation,
    List<ReadinessCriteria> Criteria,
    DateTime? EstimatedReadyDateLow,
    DateTime? EstimatedReadyDateHigh,
    bool CurrentlyEnabled,
    FeatureRiskLevel RiskLevel,
    MarketRegime? Regime = null);

public record DataMilestone(
    string Title,
    string Description,
    DateTime? EstimatedDate,
    string? EstimatedDateRange,
    MilestoneStatus Status);

public interface IReadinessAssessmentService
{
    Task<ReadinessReport> AssessAsync(int accountId, CancellationToken ct = default);
}
