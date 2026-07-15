namespace SwingTrader.Api.Services;

// Per-worker staleness thresholds keyed to each worker's real cadence, so
// "stale" means "missed its expected run", not merely "idle between runs".
// Shared by the admin Health tab (MonitoringService) and the API health
// check (WorkerHealthCheck) - they previously each had their own idea of
// stale, and both flagged weekly workers (CandleSync, Watchlist) red for
// most of every week against a daily threshold.
public static class WorkerCadence
{
    private static readonly Dictionary<string, int> StaleThresholdMinutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Monitor"] = 20,         // ~5-min cadence during market hours
        ["Execution"] = 1560,     // daily (~26h)
        ["Research"] = 1560,      // daily
        ["ResearchMidday"] = 1560,
        ["Report"] = 1560,        // daily
        ["Refinement"] = 46080,   // monthly (15th) - ~32 days
        ["Watchlist"] = 11520,    // weekly Sunday (~8 days)
        ["CandleSync"] = 11520,   // weekly Saturday (~8 days)
        ["FilingSync"] = 5760,    // weekdays 18:00 ET (~4 days, covers long weekends)
        ["BellwetherSync"] = 5760, // weekdays 6:00 ET (~4 days, covers long weekends)
    };

    // Unknown worker names fall back to the daily threshold.
    private const int DefaultStaleThresholdMinutes = 1560;

    public static int ThresholdMinutesFor(string workerName) =>
        StaleThresholdMinutes.GetValueOrDefault(workerName, DefaultStaleThresholdMinutes);

    public static bool IsStale(string workerName, DateTime lastHeartbeatUtc, DateTime utcNow) =>
        (utcNow - lastHeartbeatUtc).TotalMinutes > ThresholdMinutesFor(workerName);
}
