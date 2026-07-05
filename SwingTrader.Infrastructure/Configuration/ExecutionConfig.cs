namespace SwingTrader.Infrastructure.Configuration;

public class ExecutionConfig
{
    public const string SectionName = "Execution";

    public int RunHourEastern { get; set; } = 9;
    public int RunMinuteEastern { get; set; } = 20;
    public int MaxOrdersPerDay { get; set; } = 5;
    public int DelayBetweenOrdersSeconds { get; set; } = 2;
    public decimal CashBufferPct { get; set; } = 0.02m;
}
