namespace SwingTrader.Infrastructure.Configuration;

public class WatchlistConfig
{
    public const string SectionName = "Watchlist";

    public string RefreshDayOfWeek { get; set; } = "Sunday";
    public int RefreshHourEastern { get; set; } = 20;
    public int MaxCandidatesForClaude { get; set; } = 80;
    public decimal MinPrice { get; set; } = 15.00m;
    public decimal MaxPrice { get; set; } = 500.00m;
    // Liquidity floor: 20-day average volume x current price must be at least
    // this (GBP-agnostic, quoted in USD like the prices). Replaces the old
    // MinDailyVolume share-count knob, which was never actually applied -
    // dollar volume is what determines whether a sized position can exit at
    // target without moving the market. Matters most for the S&P 400/600
    // small caps now in the universe. $10m/day is a conservative floor for
    // the position sizes this system trades.
    public decimal MinDollarVolume { get; set; } = 10_000_000m;
    // Union screen (docs/screener-union-plan). When on, the universe is first
    // narrowed to per-setup candidates from the local candle store, so each
    // detector gets candidates on its own terms rather than sharing one
    // "biggest movers" ranking that only VolumeSpike and Breakout fit.
    public bool UnionScreenEnabled { get; set; } = true;

    // Candidates taken from EACH setup's pool before the union. Five of these
    // replace the six weights a blended composite would have needed, and each
    // is testable against its own setup's trades rather than only in concert.
    public int PerSetupCandidates { get; set; } = 24;

    // Below this the union is treated as broken (stale or empty candle store)
    // and the screen falls back to the full universe.
    public int MinUnionCandidates { get; set; } = 25;

    public int UnionHistoryDays { get; set; } = 150;
    public int UnionMinBars { get; set; } = 40;

    public decimal MinAbsChangePercent { get; set; } = 1.0m;
    public decimal MaxAbsChangePercent { get; set; } = 15.0m;

    // Dynamic screening universe (MarketUniverseService) — replaces the old
    // hardcoded StockUniverse symbol list with S&P 500/Nasdaq-100
    // constituents (via Wikipedia - see WikipediaIndexClient), so the
    // universe stays current and captures index-rebalance momentum
    // automatically instead of going stale.
    public int UniverseCacheDays { get; set; } = 7;
    public decimal TopMoverOrderBoost { get; set; } = 1.2m;

    // Qualitative AI watchlist (docs/qualitative-watchlist-plan): weekly
    // Claude picks over the WHOLE universe on narrative grounds - the lens
    // the technical screener structurally lacks. The list is created
    // disabled so picks are reviewable before they cost research; a themed
    // list is a probe, not a portfolio, hence the small default size.
    public bool QualitativeEnabled { get; set; } = true;
    public int QualitativeSize { get; set; } = 20;
}
