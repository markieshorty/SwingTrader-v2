namespace SwingTrader.Infrastructure.Configuration;

// Second-hop news knobs (docs/second-hop-plan). The signal ships SHADOW-ONLY:
// nothing here drives money until the funnel's ForwardSecondHopWeight (SH2)
// is raised above its 0 default.
public class SecondHopConfig
{
    public const string SectionName = "SecondHop";

    // Master switch for the BellwetherSync job and the research relevance
    // pass (both degrade to null downstream when off - always safe).
    public bool Enabled { get; set; } = true;

    // Days of archived linked-company events the relevance pass considers -
    // the documented propagation window is days-to-weeks; 5 trading-ish days
    // keeps the prompt small and the signal fresh.
    public int LookbackDays { get; set; } = 5;

    // Source events weaker than this (|archived sentiment score|) propagate
    // nothing and cost nothing.
    public decimal MinSourceMagnitude { get; set; } = 0.3m;

    // Per-event decay half-life for combining into the SecondHopScore.
    public int HalfLifeTradingDays { get; set; } = 5;

    // How stale a symbol's link graph may get before the weekly refresh
    // rebuilds it.
    public int GraphMaxAgeDays { get; set; } = 30;

    // Large names where second-hop events mostly originate; their news is
    // fetched + scored daily into the sentiment archive whether or not they
    // sit on any watchlist. Index heavyweights + sector leaders + key
    // upstream suppliers.
    public List<string> Bellwethers { get; set; } =
    [
        "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "TSLA", "AVGO", "BRK.B", "JPM",
        "TSM", "ASML", "AMD", "INTC", "MU", "QCOM", "AMAT", "LRCX", "KLAC", "ARM",
        "XOM", "CVX", "UNH", "LLY", "JNJ", "PFE", "V", "MA", "WMT", "COST",
        "HD", "CAT", "BA", "GE", "UPS", "FDX", "NFLX", "CRM", "ORCL", "ADBE",
    ];
}
