namespace SwingTrader.Infrastructure.Configuration;

public class RefinementConfig
{
    public const string SectionName = "Refinement";

    public bool Active { get; set; } = false;
    public int MinTradesRequired { get; set; } = 40;
    public int AnalysisPeriodDays { get; set; } = 90;
    public int RunDayOfMonth { get; set; } = 15;
    public int RunHourEastern { get; set; } = 8;
    public int RunMinuteEastern { get; set; } = 0;
    public int MinCorrelationSampleSize { get; set; } = 20;
    public decimal MaxWeightAdjustmentPerCycle { get; set; } = 0.05m;
    public string ShadowModeLogPrefix { get; set; } = "[SHADOW] ";

    // Phase 6c Part 3 — regime-split correlation analysis and per-regime weight
    // suggestions. Off by default: needs months of regime-tagged trade history
    // (see MinRegimeSampleSize) before the breakdown is statistically meaningful.
    public bool RegimeAnalysisEnabled { get; set; } = false;
    public int MinRegimeSampleSize { get; set; } = 20;

    // Future option — regime weights currently always require manual apply regardless of this flag.
    public bool ApplyRegimeWeightsAutomatically { get; set; } = false;
}
