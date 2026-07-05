namespace SwingTrader.Infrastructure.Configuration;

public class EarningsConfig
{
    public const string SectionName = "Earnings";

    public int GateDays { get; set; } = 5;
    public int PostEarningsWindowDays { get; set; } = 3;
    public decimal EpsSurpriseThresholdPct { get; set; } = 3.0m;
    public decimal MaxBeatBoost { get; set; } = 0.8m;
    public decimal MaxMissPenalty { get; set; } = 1.0m;
}
