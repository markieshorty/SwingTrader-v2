namespace SwingTrader.Core.Constants;

public static class WatchlistLimits
{
    public const int MaxSymbolsPerWatchlist = 50;
    public const int MaxEnabledWatchlists = 10;

    // Watchlists are now three fixed lists: the AI-managed (technical) list, the
    // Claude Qualitative list, and a single user-owned CUSTOM (manual) list.
    // Only manual lists can be created, and only one, so the whole set is
    // bounded and each list is independently size-capped (no shared union cap).
    public const int MaxCustomWatchlists = 1;
}
