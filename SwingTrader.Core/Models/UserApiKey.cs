namespace SwingTrader.Core.Models;

// Scoped by AccountId (not a raw UserId, unlike the original single-tenant
// spec) so every AppUser on the same Account shares one set of keys -
// consistent with how WatchlistItem/StrategyWeights are scoped.
public class UserApiKey : BaseEntity
{
    public string Provider { get; set; } = string.Empty;
    // "Finnhub", "Tiingo", "Trading212Key", "Trading212Secret", "Claude",
    // "EmailUsername", "EmailPassword"
    public string EncryptedValue { get; set; } = string.Empty;
    public string EncryptedDek { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public DateTime? LastTestedAt { get; set; }
    public string? LastTestResult { get; set; }
}

public static class ApiKeyProviders
{
    public const string Finnhub = "Finnhub";
    public const string Tiingo = "Tiingo";
    public const string Trading212Key = "Trading212Key";
    public const string Trading212Secret = "Trading212Secret";
    public const string Claude = "Claude";
    public const string EmailUsername = "EmailUsername";
    public const string EmailPassword = "EmailPassword";

    public static readonly string[] All =
    [
        Finnhub, Tiingo, Trading212Key, Trading212Secret, Claude, EmailUsername, EmailPassword,
    ];
}
