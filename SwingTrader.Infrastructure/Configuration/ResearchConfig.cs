namespace SwingTrader.Infrastructure.Configuration;

public class ResearchConfig
{
    public const string SectionName = "Research";

    public int RunHourEastern { get; set; } = 6;
    public int MaxConcurrentSymbols { get; set; } = 1;
    public int CandleHistoryDays { get; set; } = 60;
    public int NewsLookbackDays { get; set; } = 3;
    public int MaxNewsArticles { get; set; } = 5;
    public decimal MinConvictionForBuy { get; set; } = 6.0m;
    public decimal MinConvictionForWatch { get; set; } = 5.0m;

    // Enables the 12:30 ET rescore slot in SchedulerFunction (which reads the
    // raw "Research:MiddayRescoreEnabled" config key). Off by default: only
    // worth it on a Tiingo plan fast enough to rescore the universe in
    // minutes, and it re-baselines intraday signal timestamps.
    public bool MiddayRescoreEnabled { get; set; } = false;
}
