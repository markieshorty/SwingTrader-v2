namespace SwingTrader.Infrastructure.Configuration;

public class PriceLevelConfig
{
    public const string SectionName = "PriceLevel";

    public int LookbackDays { get; set; } = 120;
    public int MinCandles { get; set; } = 20;
    public decimal ProximityPct { get; set; } = 2.0m;
    public decimal ClusterPct { get; set; } = 1.5m;
    public decimal BreakoutVolumeRatio { get; set; } = 1.3m;
}
