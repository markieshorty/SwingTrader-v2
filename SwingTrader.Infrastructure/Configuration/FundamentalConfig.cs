namespace SwingTrader.Infrastructure.Configuration;

public class FundamentalConfig
{
    public const string SectionName = "Fundamental";

    public int AnalystLookbackMonths { get; set; } = 3;
    public int InsiderLookbackDays { get; set; } = 90;
    public int EarningsHistoryQuarters { get; set; } = 4;
    public int MinAnalystsForTrend { get; set; } = 3;
    public int CacheDurationDays { get; set; } = 7;
    // Rebalanced across the three active inputs while RevenueEstimatesEnabled is off (below) —
    // with Revenue always neutral (0.5) at its original 0.15 weight, every score carried a
    // fixed 0.075 "neutral tax" instead of being driven purely by the signals actually
    // available. Original values (restore if RevenueEstimatesEnabled is turned back on):
    // Analyst 0.35, Insider 0.30, Earnings 0.20, Revenue 0.15.
    public decimal AnalystSubWeight { get; set; } = 0.40m;
    public decimal InsiderSubWeight { get; set; } = 0.35m;
    public decimal EarningsSubWeight { get; set; } = 0.25m;
    public decimal RevenueSubWeight { get; set; } = 0.00m;

    // Off by default — /stock/revenue-estimate requires Finnhub's paid Fundamental-1 tier
    // ($50/mo). No point calling an endpoint that always 403s; flip this on once/if that
    // plan is purchased. RevenueDirection stays Insufficient (neutral) while disabled.
    public bool RevenueEstimatesEnabled { get; set; } = false;

    // MSPR (Finnhub /stock/insider-sentiment, free tier): the aggregated
    // Monthly Share Purchase Ratio (-100..100) - a less noisy read on
    // insider conviction than raw P/S transaction clustering. A strongly
    // agreeing MSPR upgrades the clustering classification one notch, a
    // strongly disagreeing one downgrades it; anything between the
    // thresholds leaves it alone. Fetch failure = clustering-only, exactly
    // the pre-MSPR behaviour.
    public int InsiderMsprLookbackMonths { get; set; } = 3;
    public decimal MsprBullishThreshold { get; set; } = 20m;
    public decimal MsprBearishThreshold { get; set; } = -20m;

    // Earnings surprise ACCELERATION: beats getting bigger vs shrinking,
    // from the same 4-quarter history the consistency tier already uses.
    // Applied as a bounded adjustment to the earnings sub-score (a ±20pp
    // surprise trend saturates the cap) so the trend can tilt but never
    // override the beat-count tier.
    public decimal SurpriseAccelerationMaxAdjust { get; set; } = 0.15m;

    // Analyst revision VELOCITY: net-bullishness change across the lookback
    // window, from the same /stock/recommendation data. The level mostly
    // lags price; the change leads it. Velocity >= Strong (or >= Bullish
    // with an already-positive level) drives the Strongly* tiers.
    public decimal AnalystVelocityBullish { get; set; } = 0.10m;
    public decimal AnalystVelocityStrong { get; set; } = 0.25m;
}
