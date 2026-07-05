namespace SwingTrader.Infrastructure.Configuration;

public class WatchlistConfig
{
    public const string SectionName = "Watchlist";

    public string RefreshDayOfWeek { get; set; } = "Sunday";
    public int RefreshHourEastern { get; set; } = 20;
    public int MaxCandidatesForClaude { get; set; } = 80;
    public int TargetWatchlistSize { get; set; } = 25;
    public decimal MinPrice { get; set; } = 15.00m;
    public decimal MaxPrice { get; set; } = 500.00m;
    public decimal MinDailyVolume { get; set; } = 500_000m;
    public decimal MinAbsChangePercent { get; set; } = 1.0m;
    public decimal MaxAbsChangePercent { get; set; } = 15.0m;

    // Off by default — purely additive supplementary candidate source (Finnhub top
    // movers), safe to enable at any time once you want a wider candidate pool.
    public bool TopMoversEnabled { get; set; } = false;

    // Dynamic screening universe (MarketUniverseService) — replaces the old
    // hardcoded StockUniverse symbol list with live index constituents, so
    // the universe stays current and captures index-rebalance momentum
    // automatically instead of going stale.
    public List<string> IndexSymbols { get; set; } = ["^GSPC", "^NDX"];
    public int UniverseCacheDays { get; set; } = 7;
    public decimal TopMoverOrderBoost { get; set; } = 1.2m;
}
