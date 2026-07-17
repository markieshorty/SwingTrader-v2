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

    // Provenance: which tool proposed this weight change. Strategy Lab applies
    // are recorded here too (created Applied in one step, no pending detour)
    // so the refinement page carries the complete audit trail of every
    // production weight change. Defaults to AutoRefinement for old rows.
    public RefinementOrigin Origin { get; set; } = RefinementOrigin.AutoRefinement;
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

    // Risk-setting overrides that rode this suggestion (Strategy Lab applies
    // that carried rule/tactic winners): camelCase JSON of
    // { targetRegime, autopause?, rules } where rules is the run's
    // HistoricTradingRules. Null for weight-only suggestions. Recorded so the
    // Refinement page - the audit trail for production changes - shows the
    // risk half of an apply, not just the weights half.
    public string? SuggestedRiskRulesJson { get; set; }

    public decimal MarketAdjustedWinRate { get; set; }
    public bool UnusualMarketConditions { get; set; }
    public string? MarketConditionWarning { get; set; }

    // Replay check: correlation only proposes a *direction* per component - it
    // never verifies what the whole suggested config would have done. Before a
    // suggestion is offered, the same trades are replayed under current vs
    // suggested weights (the Strategy Lab's evaluator); a suggestion whose
    // replay is WORSE than current gets confidence forced to Low and this flag
    // set false so the UI can warn. Null on suggestions predating the check.
    public decimal? ReplayCurrentAvgReturnPct { get; set; }
    public decimal? ReplaySuggestedAvgReturnPct { get; set; }
    public int? ReplayTradesKept { get; set; }
    public bool? ReplayCheckPassed { get; set; }
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
