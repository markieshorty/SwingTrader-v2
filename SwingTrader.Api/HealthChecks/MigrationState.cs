namespace SwingTrader.Api.HealthChecks;

// Captured once at startup: whether EF migrations applied cleanly. A singleton
// so MigrationHealthCheck can read it. Its whole purpose is to stop a revision
// whose migrations FAILED from taking traffic: readiness stays Unhealthy, so
// Azure Container Apps holds the previous (working) revision instead of
// silently serving new code against a stale schema (the failure mode behind
// the 15:35 TargetWatchlistSize incident). Starts un-applied; the startup path
// flips it exactly once.
public sealed class MigrationState
{
    private volatile bool _applied;
    private volatile string? _error;

    public bool Applied => _applied;
    public string? Error => _error;

    public void MarkApplied()
    {
        _error = null;
        _applied = true;
    }

    public void MarkFailed(string error)
    {
        _error = error;
        _applied = false;
    }
}
