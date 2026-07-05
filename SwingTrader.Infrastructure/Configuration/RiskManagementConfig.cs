namespace SwingTrader.Infrastructure.Configuration;

public class RiskManagementConfig
{
    public const string SectionName = "RiskManagement";
    public bool Active { get; set; } = false;
    public int EvaluationDayOfMonth { get; set; } = 1;
    public int EvaluationHourEastern { get; set; } = 9;
    public int EvaluationMinuteEastern { get; set; } = 0;
    public string ShadowModeLogPrefix { get; set; } = "[SHADOW] ";
}
