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

    // Slot-aware stage-2 skip (docs/on-demand-research P1): when the account
    // has no free position slot (or entries are paused), skip the expensive
    // stage-2 scoring (sentiment/fundamentals/second-hop) for EVERY symbol
    // that day - conviction-grade scores are unactionable with nowhere to
    // put a Buy. Stage one (the gate) always runs, keeping the shadow
    // evidence gap-free. Default ON (31 Jul 2026, owner's call — diagnose
    // phase); set Research:SlotAwareStageTwo=false to restore always-on
    // stage-2 scoring.
    public bool SlotAwareStageTwo { get; set; } = true;

    // Sentiment momentum: blends the DIRECTION of a symbol's sentiment (today
    // vs its own recent archive average) into the level Claude scored today.
    // Improving sentiment is a leading signal the ~3-day snapshot alone
    // misses, and it costs nothing - the SentimentDailyScore archive is
    // already accruing daily. Only applied once a symbol has MinHistory
    // prior scores; below that the raw level passes through untouched.
    public decimal SentimentMomentumWeight { get; set; } = 0.30m;
    public int SentimentMomentumLookbackDays { get; set; } = 7;
    public int SentimentMomentumMinHistory { get; set; } = 3;

    // Catalyst detection: the sentiment Claude call also extracts DATED
    // forward-looking events (guidance raises, product launches, FDA
    // decisions, contract wins) from the same articles; a detected catalyst
    // applies a bounded conviction adjustment, mirroring the post-earnings
    // adjustment. Earnings dates themselves are excluded - the earnings gate
    // owns those. Bounded so a misread catalyst can never dominate the
    // deterministic component blend.
    public bool CatalystDetectionEnabled { get; set; } = true;
    public decimal MaxCatalystBoost { get; set; } = 0.5m;
    public decimal MaxCatalystPenalty { get; set; } = 0.5m;
    public int CatalystMaxDaysAhead { get; set; } = 30;

}
