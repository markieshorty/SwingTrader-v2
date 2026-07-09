namespace SwingTrader.Api;

// Named rate-limit policies, referenced both where they're registered
// (Program.cs AddRateLimiter) and where individual endpoints opt in via
// RequireRateLimiting(...). Kept as shared constants so the string names can
// never drift apart between registration and use.
//
//   ClaudeJobs   - POST /run/{jobType}: each manual research/report/
//                  refinement run fans out many Claude calls (the user's own
//                  Anthropic spend), so this is deliberately tight. The
//                  automated scheduler is the intended trigger; manual runs
//                  are for testing, where a handful per window is plenty and
//                  an accidental double-click or a script can't rack up a bill.
//   ExternalRead - the GET endpoints that pull live Finnhub/Tiingo/T212
//                  market data per request: looser, just a sanity ceiling
//                  against a hot-looping client burning provider quota.
public static class RateLimitPolicies
{
    public const string ClaudeJobs = "claude-jobs";
    public const string ExternalRead = "external-read";
}
