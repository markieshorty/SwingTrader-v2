namespace SwingTrader.Core.Constants;

public static class WatchlistLimits
{
    public const int MaxSymbolsPerWatchlist = 50;
    public const int MaxEnabledWatchlists = 10;

    // Research scores the deduplicated union of every enabled watchlist's
    // symbols once per day (see IWatchlistRepository.GetAllEnabledSymbolsAsync)
    // - this caps that union so a single day's run stays within a sane size
    // regardless of how many watchlists happen to be enabled.
    public const int MaxTotalEnabledSymbols = 100;
}
