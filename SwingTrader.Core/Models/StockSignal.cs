using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class StockSignal : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
    // Copied from the WatchlistItem this signal was scored from (itself
    // sourced from Finnhub's company profile when the symbol was added) -
    // avoids a second Finnhub call just to display a hover tooltip.
    public string? CompanyName { get; set; }
    public DateOnly SignalDate { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal? Rsi14 { get; set; }
    public decimal? Macd { get; set; }
    public decimal? MacdSignal { get; set; }
    public decimal? MacdHistogram { get; set; }
    public decimal? VolumeRatio { get; set; }
    public decimal? BollingerUpper { get; set; }
    public decimal? BollingerLower { get; set; }
    public decimal? BollingerMid { get; set; }
    public decimal? Ema9 { get; set; }
    public decimal? Ema21 { get; set; }
    public decimal? SentimentScore { get; set; }
    public string? NewsSummary { get; set; }
    public SetupType SetupType { get; set; }
    public decimal? ConvictionScore { get; set; }
    public Recommendation Recommendation { get; set; }
    public string? Reasoning { get; set; }
    public bool WasExecuted { get; set; }

    // Component scores (0.0 to 1.0) — the inputs blended into ConvictionScore.
    public decimal? RsiScore { get; set; }
    public decimal? MacdScore { get; set; }
    public decimal? VolumeScore { get; set; }
    public decimal? SentimentComponentScore { get; set; }
    public decimal? SetupQualityScore { get; set; }
    public decimal? RelativeStrengthScore { get; set; }
    public decimal? PriceLevelScore { get; set; }

    // Relative strength
    public string? SectorEtf { get; set; }
    public decimal? StockReturn5d { get; set; }
    public decimal? SectorReturn5d { get; set; }
    public decimal? RelativeReturn { get; set; }

    // Price level memory
    public PriceLevelContext PriceLevelContext { get; set; } = PriceLevelContext.BetweenLevels;
    public decimal? NearestSupport { get; set; }
    public decimal? NearestResistance { get; set; }

    // Earnings context
    public EarningsSetupType EarningsSetupType { get; set; } = EarningsSetupType.None;
    public decimal? EpsSurprisePct { get; set; }
    public int? DaysUntilEarnings { get; set; }
    public int? DaysSinceEarnings { get; set; }

    // Calculated by Report Agent — used by Execution Agent
    public decimal? CalculatedStopLoss { get; set; }
    public decimal? CalculatedTarget { get; set; }
    public decimal? RiskRewardRatio { get; set; }

    // Market regime detected when this signal was scored
    public MarketRegime? MarketRegimeAtSignal { get; set; }

    // Fundamental momentum — forward-looking business signal, deterministic score.
    public decimal? FundamentalMomentumScore { get; set; }
    public string? FundamentalNarrative { get; set; }
    public AnalystTrend? AnalystTrend { get; set; }
    public InsiderActivity? InsiderActivity { get; set; }
    public EarningsConsistency? EarningsConsistency { get; set; }
    public RevenueDirection? RevenueDirection { get; set; }

    // Funnel shadow scores (docs/funnel-plan, Phase F1): computed alongside
    // the legacy blend but driving nothing yet. GateScore = the six
    // backtestable components only (dead pair pinned neutral - bit-identical
    // to what HistoricBacktester models), earnings-adjusted. ForwardScore =
    // sentiment+fundamental blend rescaled 0..10, catalyst-adjusted. The
    // Would* booleans are snapshotted AT SIGNAL TIME (not derived at read
    // time) so later threshold changes never rewrite history.
    public decimal? GateScore { get; set; }
    public decimal? ForwardScore { get; set; }
    public bool ForwardScoreDegraded { get; set; }
    public bool WouldPassGate { get; set; }
    public bool WouldBeVetoed { get; set; }

    // Filing-delta shadow (docs/filing-delta-plan Phase FD1): the symbol's
    // most recent scored filing-language change, decayed to signal date
    // (half-life ~63 trading days). Drives nothing until ForwardFilingWeight
    // is raised above 0 (FD2). Null = no scored filing history yet.
    public decimal? FilingDeltaScore { get; set; }
    public string? FilingDeltaSummary { get; set; }

    // Second-hop news shadow (docs/second-hop-plan Phase SH1): scored events
    // at economically LINKED companies, propagated to this symbol with a
    // 5-trading-day half-life. Drives nothing until ForwardSecondHopWeight
    // is raised above 0 (SH2). Null = no links / no qualifying events.
    public decimal? SecondHopScore { get; set; }
    public string? SecondHopSummary { get; set; }

    // Cross-sectional selection percentile of this symbol at watchlist-pick
    // time (0-100 vs that week's screened universe; null pre-feature or for
    // manual watchlist adds). Shadow metadata - drives nothing yet.
    public decimal? SelectionPercentile { get; set; }
}
