namespace SwingTrader.Infrastructure.Configuration;

// Model/MaxTokens are shared tuning parameters, not secrets - the actual
// per-account (or shared-fallback) API key comes from IUserKeyService.
public class ClaudeConfig
{
    public const string SectionName = "Claude";

    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; set; } = 4000;

    // Per-agent overrides for the two Claude calls that do open-ended,
    // low-frequency synthesis (curating ~25 picks from ~600 candidates with
    // soft constraints; assessing correlation/regime findings once a month)
    // rather than high-frequency structured extraction or narrating an
    // already-deterministic decision - those stay on the cheaper default.
    public string? WatchlistModel { get; set; } = "claude-sonnet-4-5-20250929";
    public string? RefinementModel { get; set; } = "claude-sonnet-4-5-20250929";
}
