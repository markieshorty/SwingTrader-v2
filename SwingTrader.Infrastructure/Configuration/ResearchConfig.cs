namespace SwingTrader.Infrastructure.Configuration;

public class ResearchConfig
{
    public const string SectionName = "Research";

    public int RunHourEastern { get; set; } = 6;
    public int MaxConcurrentSymbols { get; set; } = 1;
    public int CandleHistoryDays { get; set; } = 60;
    public int NewsLookbackDays { get; set; } = 3;
    // Raised 5 -> 10 with the second news source (Tiingo) - one Claude call
    // either way, the articles block just gets richer.
    public int MaxNewsArticles { get; set; } = 10;
    // Blends Tiingo's ticker-tagged news feed into the sentiment prompt
    // alongside Finnhub. Either source failing degrades to the other.
    public bool TiingoNewsEnabled { get; set; } = true;
    // Sentiment archive: article METADATA is pruned after this many months
    // (piggybacked on the weekly candle-sync job); daily SCORES are kept
    // forever - they're tiny and they're the dataset this exists to build.
    public int ArchiveRetentionMonths { get; set; } = 24;
    public decimal MinConvictionForBuy { get; set; } = 6.0m;
    public decimal MinConvictionForWatch { get; set; } = 5.0m;

    // Enables the 12:30 ET rescore slot in SchedulerFunction (which reads the
    // raw "Research:MiddayRescoreEnabled" config key). Off by default: only
    // worth it on a Tiingo plan fast enough to rescore the universe in
    // minutes, and it re-baselines intraday signal timestamps.
    public bool MiddayRescoreEnabled { get; set; } = false;
}
