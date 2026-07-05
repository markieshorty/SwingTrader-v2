namespace SwingTrader.Core.Models;

// Scoped by AccountId (not a raw UserId, unlike the original single-tenant
// spec) so every AppUser on the same Account shares one set of keys -
// consistent with how WatchlistItem/StrategyWeights are scoped.
public class UserApiKey : BaseEntity
{
    public string Provider { get; set; } = string.Empty;
    // "Finnhub", "Tiingo", "Trading212DemoKey", "Trading212DemoSecret",
    // "Trading212LiveKey", "Trading212LiveSecret", "Claude"
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

    // Trading212 issues separate API credentials per environment - a demo
    // key is rejected by the live endpoint and vice versa - so these are
    // stored (and tested) as two independent pairs rather than one pair
    // reused across both, letting an account keep both parked at once and
    // switch TradingMode without re-entering keys.
    public const string Trading212DemoKey = "Trading212DemoKey";
    public const string Trading212DemoSecret = "Trading212DemoSecret";
    public const string Trading212LiveKey = "Trading212LiveKey";
    public const string Trading212LiveSecret = "Trading212LiveSecret";

    public const string Claude = "Claude";

    // No per-account email credentials - report/alert delivery uses a single
    // platform-level SMTP relay (Email:SmtpHost/Username/Password in Key
    // Vault, see EmailService), not one the user sets up themselves.
    public static readonly string[] All =
    [
        Finnhub, Tiingo,
        Trading212DemoKey, Trading212DemoSecret, Trading212LiveKey, Trading212LiveSecret,
        Claude,
    ];
}
