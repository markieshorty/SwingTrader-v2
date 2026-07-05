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
    Task<TClient> CreateClaudeAsync<TClient>(int accountId, CancellationToken ct = default);
}
