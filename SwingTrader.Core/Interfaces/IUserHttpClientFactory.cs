using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Interfaces;

// Each call decrypts the account's key(s) via IUserKeyService and builds a
// fresh Refit client with them baked into the HttpClient's auth header -
// unlike the legacy single-tenant app, these can't be DI-registered
// singletons since every account has different credentials.
public interface IUserHttpClientFactory
{
    Task<TClient> CreateFinnhubAsync<TClient>(int accountId, CancellationToken ct = default);
    Task<TClient> CreateTiingoAsync<TClient>(int accountId, CancellationToken ct = default);
    Task<TClient> CreateTrading212Async<TClient>(int accountId, CancellationToken ct = default);

    // Builds a T212 client for a specific mode's key pair + host, ignoring
    // the account's current TradingMode - used to verify a Demo or Live pair
    // independently (e.g. testing Live keys during onboarding while the
    // account is still in the default Demo mode).
    Task<TClient> CreateTrading212ForModeAsync<TClient>(int accountId, TradingMode mode, CancellationToken ct = default);

    Task<TClient> CreateClaudeAsync<TClient>(int accountId, CancellationToken ct = default);
}
