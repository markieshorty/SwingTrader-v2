namespace SwingTrader.Infrastructure.Configuration;

public class MonitorConfig
{
    public const string SectionName = "Monitor";

    public int PollIntervalMinutes { get; set; } = 5;
    public double TrailingActivationPct { get; set; } = 0.05;
    public double TrailingDistancePct { get; set; } = 0.03;
    public int MaxHoldDays { get; set; } = 10;
}
