using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Interfaces;

// Result of a key connectivity test. For Trading212 pairs the extra fields
// are populated from a real account call so the user can eyeball that the
// key/secret are the right way around and pointed at the right environment:
// CashTotal/CashFree/Currency come back from T212, and IsDemo reflects which
// environment the tested pair authenticated against (fake vs real money).
// All extra fields are null for non-T212 providers and for pairs that only
// decrypted (couldn't make a live call).
public record KeyTestResult(
    bool Valid,
    string Message,
    bool? IsDemo = null,
    decimal? CashTotal = null,
    decimal? CashFree = null,
    string? Currency = null);

public interface IUserKeyService
{
    // Throws if the account hasn't set this provider's key (and, for
    // Claude only, no shared fallback key is configured).
    Task<string> GetKeyAsync(int accountId, string provider, CancellationToken ct = default);

    Task SaveKeyAsync(int accountId, string provider, string plaintext, CancellationToken ct = default);

    // Connectivity check. For a complete Trading212 pair the result carries
    // the account's cash balance + which environment it hit, so the caller
    // can confirm the credentials are correct and not swapped. Other
    // providers return Valid + Message only.
    Task<KeyTestResult> TestKeyAsync(int accountId, string provider, CancellationToken ct = default);

    // Tests a whole Trading212 pair (key + secret) for one mode against that
    // mode's endpoint, regardless of the account's current TradingMode, and
    // records the result on both the key and secret rows. Returns the cash
    // balance + environment so the user can confirm the pair connects.
    Task<KeyTestResult> TestTrading212PairAsync(int accountId, TradingMode mode, CancellationToken ct = default);

    Task<Dictionary<string, KeyStatus>> GetKeyStatusesAsync(int accountId, CancellationToken ct = default);

    Task DeleteKeyAsync(int accountId, string provider, CancellationToken ct = default);
}
