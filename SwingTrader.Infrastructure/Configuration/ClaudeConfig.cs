namespace SwingTrader.Infrastructure.Configuration;

// Model/MaxTokens are shared tuning parameters, not secrets - the actual
// per-account (or shared-fallback) API key comes from IUserKeyService.
public class ClaudeConfig
{
    public const string SectionName = "Claude";

    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; set; } = 4000;

    // The high-value, GENUINELY LOW-FREQUENCY judgement calls run on the
    // premium model: watchlist selection (both lists, weekly), refinement
    // analysis (monthly), Lab analysis/sweep explanation (on-demand),
    // filing-delta diff scoring (only on an actual filing change),
    // economic-link graph building (once per symbol per ~30 days).
    // Everything else - including anything that runs per-symbol-per-day -
    // stays on the cheap default Model: per-symbol sentiment/catalyst,
    // fundamentals, bellwether levels, and the second-hop relevance pass
    // (initially misrouted to premium 13 Jul 2026, corrected the same day
    // once an unexpectedly large API bill made clear it runs once per
    // watchlist symbol EVERY research cycle - the same cardinality as
    // sentiment, not a low-frequency synthesis call).
    //
    // Sonnet, not Opus (Mark, 14 Jul 2026): these calls are qualitative
    // judgement over text, not maths or code - Opus's premium wasn't
    // buying anything here.
    public string PremiumModel { get; set; } = "claude-sonnet-5";

    // Per-agent overrides; null = PremiumModel. Kept for targeted tuning.
    public string? WatchlistModel { get; set; }
    public string? RefinementModel { get; set; }
}
