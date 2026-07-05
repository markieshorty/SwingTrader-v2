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
}
