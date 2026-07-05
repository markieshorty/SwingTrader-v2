using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Interfaces;

public interface IUserKeyService
{
    // Throws if the account hasn't set this provider's key (and, for
    // Claude only, no shared fallback key is configured).
    Task<string> GetKeyAsync(int accountId, string provider, CancellationToken ct = default);

    Task SaveKeyAsync(int accountId, string provider, string plaintext, CancellationToken ct = default);

    // Real provider connectivity checks land once the corresponding HTTP
    // client (Finnhub/Tiingo/T212/Claude) is ported - until then this only
    // validates that a non-empty value round-trips through decryption.
    Task<bool> TestKeyAsync(int accountId, string provider, CancellationToken ct = default);

    Task<Dictionary<string, KeyStatus>> GetKeyStatusesAsync(int accountId, CancellationToken ct = default);

    Task DeleteKeyAsync(int accountId, string provider, CancellationToken ct = default);
}
