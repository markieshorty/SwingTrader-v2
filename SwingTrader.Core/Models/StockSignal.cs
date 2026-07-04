using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class StockSignal : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
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
}
