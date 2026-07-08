using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class RefinementSuggestion : BaseEntity
{
    // The mode whose trade history this analysis was computed from (see
    // PortfolioSnapshot.TradingMode) - prevents Demo and Live performance
    // from being blended into a single weight-adjustment suggestion.
    public TradingMode TradingMode { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateOnly AnalysisPeriodStart { get; set; }
    public DateOnly AnalysisPeriodEnd { get; set; }
    public int TradeCountAnalysed { get; set; }
    public int WinnerCount { get; set; }
    public int LoserCount { get; set; }
    public decimal OverallWinRate { get; set; }
    public string CurrentWeightsJson { get; set; } = string.Empty;
    public string SuggestedWeightsJson { get; set; } = string.Empty;
    public string ComponentAnalysisJson { get; set; } = string.Empty;
    public string? AssessmentSummary { get; set; }
    public RefinementConfidenceLevel ConfidenceLevel { get; set; }
    public RefinementStatus Status { get; set; } = RefinementStatus.Pending;
    public DateTime? AppliedAt { get; set; }
    public int? AppliedWeightsId { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionNote { get; set; }
    public bool IsShadowMode { get; set; }

    // Regime-split analysis — JSON of Dictionary<MarketRegime, RegimeAnalysis>; null when
    // regime analysis is disabled or no trades carried regime tags yet.
    public string? RegimeBreakdownJson { get; set; }

    // JSON of Dictionary<MarketRegime, StrategyWeights>; null unless at least one regime hit
    // the minimum regime sample size.
    public string? SuggestedRegimeWeightsJson { get; set; }

    public decimal MarketAdjustedWinRate { get; set; }
    public bool UnusualMarketConditions { get; set; }
    public string? MarketConditionWarning { get; set; }
}

// Serialized (as part of RegimeBreakdownJson) — not a mapped entity.
public record RegimeAnalysis(
    MarketRegime Regime,
    int TradeCount,
    decimal WinRate,
    decimal AvgSpyReturn,
    decimal AvgMarketAdjustedReturn,
    List<ComponentFinding> Findings,
    bool HasSufficientData,
    string? InsufficientDataReason);

// Serialized (as part of ComponentAnalysisJson) — not a mapped entity.
public record ComponentFinding(
    string ComponentName,
    decimal CurrentWeight,
    decimal WinnerAvgScore,
    decimal LoserAvgScore,
    decimal Correlation,
    decimal SuggestedWeight,
    decimal WeightDelta,
    string Reasoning);
