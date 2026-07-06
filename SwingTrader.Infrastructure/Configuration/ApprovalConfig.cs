namespace SwingTrader.Infrastructure.Configuration;

public class ApprovalConfig
{
    public const string SectionName = "Approval";

    public string BaseUrl { get; set; } = "http://localhost:5001";

    /// <summary>
    /// Minutes after market open (9:30 ET) that approval window closes.
    /// Default: 9:15 AM ET = window closes 15 min before open.
    /// </summary>
    public int ApprovalWindowCloseHourEt { get; set; } = 9;
    public int ApprovalWindowCloseMinuteEt { get; set; } = 15;
}
