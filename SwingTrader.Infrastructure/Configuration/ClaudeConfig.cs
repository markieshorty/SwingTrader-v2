namespace SwingTrader.Infrastructure.Configuration;

// Model/MaxTokens are shared tuning parameters, not secrets - the actual
// per-account (or shared-fallback) API key comes from IUserKeyService.
public class ClaudeConfig
{
    public const string SectionName = "Claude";

    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; set; } = 4000;

    // The high-value, low-frequency judgement calls run on Opus (Mark,
    // 13 Jul 2026 - token volume on these paths is minimal, so the extra
    // intelligence is affordable): watchlist selection (both lists),
    // refinement analysis, Lab analysis/sweep explanation, filing-delta
    // diff scoring, economic-link graph building, second-hop relevance.
    // High-volume structured extraction (per-symbol sentiment/catalyst,
    // fundamentals, bellwether levels) stays on the cheap default Model.
    public string PremiumModel { get; set; } = "claude-opus-4-8";

    // Per-agent overrides; null = PremiumModel. Kept for targeted tuning.
    public string? WatchlistModel { get; set; }
    public string? RefinementModel { get; set; }
}
