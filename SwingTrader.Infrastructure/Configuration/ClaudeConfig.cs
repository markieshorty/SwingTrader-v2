namespace SwingTrader.Infrastructure.Configuration;

// Model/MaxTokens are shared tuning parameters, not secrets - the actual
// per-account (or shared-fallback) API key comes from IUserKeyService.
public class ClaudeConfig
{
    public const string SectionName = "Claude";

    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; set; } = 4000;
}
