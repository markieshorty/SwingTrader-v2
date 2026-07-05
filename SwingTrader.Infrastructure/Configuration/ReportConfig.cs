namespace SwingTrader.Infrastructure.Configuration;

public class ReportConfig
{
    public const string SectionName = "Report";

    public int RunHourEastern { get; set; } = 6;
    public int RunMinuteEastern { get; set; } = 30;
    public int MaxBuysInReport { get; set; } = 3;
    public int MaxWatchesInReport { get; set; } = 5;
    public double OpenPositionWarningPctFromStop { get; set; } = 2.0;
    public double OpenPositionWarningPctFromTarget { get; set; } = 2.0;
    public int TimeExitWarningDays { get; set; } = 7;
}
