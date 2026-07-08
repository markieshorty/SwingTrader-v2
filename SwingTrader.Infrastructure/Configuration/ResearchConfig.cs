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
}
