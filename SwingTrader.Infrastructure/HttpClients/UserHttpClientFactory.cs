using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Refit;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Security;

namespace SwingTrader.Infrastructure.HttpClients;

// Depends on the raw key-storage pieces (repository + encryption) rather
// than IUserKeyService directly - UserKeyService's connectivity tests call
// back into this factory to build a real client, so depending on
// IUserKeyService here would be a circular constructor dependency that .NET's
// DI container rejects at resolution time (this shipped broken until caught
// via the "circular dependency detected for IUserKeyService" exception).
public class UserHttpClientFactory(
    IUserApiKeyRepository repository,
    IKeyEncryptionService encryption,
    IAccountRepository accounts,
    IConfiguration config,
    ILogger<UserHttpClientFactory> logger) : IUserHttpClientFactory
{
    private const string FinnhubBaseUrl = "https://finnhub.io/api/v1";
    private const string TiingoBaseUrl = "https://api.tiingo.com";
    private const string ClaudeBaseUrl = "https://api.anthropic.com";
    private const string Trading212DemoBaseUrl = "https://demo.trading212.com";
    private const string Trading212LiveBaseUrl = "https://live.trading212.com";

    // None of these clients set an explicit Timeout, so they silently used
    // HttpClient's default of exactly 100 seconds - indistinguishable at the
    // call site from a genuine cancellation, since HttpClient.Timeout
    // expiring throws the same TaskCanceledException. A slow-but-live Tiingo
    // response (observed during the 50/hour throttle rollout) was hitting
    // that ceiling and getting logged as "The operation was canceled",
    // burning through the rest of the run's per-symbol tasks one by one.
    private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(3);

    public async Task<TClient> CreateFinnhubAsync<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await GetDecryptedKeyAsync(accountId, ApiKeyProviders.Finnhub, ct);
        // Finnhub authenticates via a "token" query parameter rather than a
        // header, so each request is signed by this handler instead of a
        // fixed default header.
        var handler = new FinnhubTokenHandler(apiKey) { InnerHandler = new HttpClientHandler() };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(FinnhubBaseUrl), Timeout = HttpClientTimeout };
        return RestService.For<TClient>(httpClient);
    }

    public async Task<TClient> CreateTiingoAsync<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await GetDecryptedKeyAsync(accountId, ApiKeyProviders.Tiingo, ct);
        var httpClient = new HttpClient { BaseAddress = new Uri(TiingoBaseUrl), Timeout = HttpClientTimeout };
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
        return RestService.For<TClient>(httpClient);
    }

    public async Task<TClient> CreateTrading212Async<TClient>(int accountId, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        var isLive = account.TradingMode == TradingMode.Live;
        var keyProvider = isLive ? ApiKeyProviders.Trading212LiveKey : ApiKeyProviders.Trading212DemoKey;
        var secretProvider = isLive ? ApiKeyProviders.Trading212LiveSecret : ApiKeyProviders.Trading212DemoSecret;

        var apiKey = await GetDecryptedKeyAsync(accountId, keyProvider, ct);
        var apiSecret = await GetDecryptedKeyAsync(accountId, secretProvider, ct);

        var baseUrl = isLive ? Trading212LiveBaseUrl : Trading212DemoBaseUrl;
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));
        var handler = new T212DiagnosticHandler(logger) { InnerHandler = new HttpClientHandler() };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = HttpClientTimeout };
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");
        return RestService.For<TClient>(httpClient);
    }

    public async Task<TClient> CreateClaudeAsync<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await GetDecryptedKeyAsync(accountId, ApiKeyProviders.Claude, ct);
        var httpClient = new HttpClient { BaseAddress = new Uri(ClaudeBaseUrl), Timeout = HttpClientTimeout };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return RestService.For<TClient>(httpClient);
    }

    // Mirrors UserKeyService.GetKeyAsync (Claude falls back to a shared
    // config key when no per-account key is saved) - kept in sync manually
    // since the two can't share an implementation without reintroducing the
    // circular dependency described above.
    private async Task<string> GetDecryptedKeyAsync(int accountId, string provider, CancellationToken ct)
    {
        var key = await repository.GetAsync(accountId, provider, ct);
        if (key is not null)
            return await encryption.DecryptAsync(accountId, key.EncryptedValue, key.EncryptedDek, ct);

        if (provider == ApiKeyProviders.Claude)
        {
            var shared = config["Claude:ApiKey"];
            if (!string.IsNullOrEmpty(shared)) return shared;
        }

        throw new InvalidOperationException($"No {provider} key configured for account {accountId}.");
    }

    private class FinnhubTokenHandler(string token) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uriBuilder = new UriBuilder(request.RequestUri!);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            query["token"] = token;
            uriBuilder.Query = query.ToString();
            request.RequestUri = uriBuilder.Uri;
            return await base.SendAsync(request, ct);
        }
    }

    // Logs the real T212 status code + body instead of relying on Refit's
    // thrown-exception path for visibility - a prior incident (T212 returning
    // 200 OK with a zeroed-out Cash object during rate-limiting) could only be
    // diagnosed by reasoning about which catch block didn't fire, since there
    // was no actual telemetry of the response. account/summary is always
    // logged (success or not) since that's the endpoint that produced the
    // ambiguous response; other endpoints only log on non-success.
    private class T212DiagnosticHandler(ILogger logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (!response.IsSuccessStatusCode || path.Contains("account/summary"))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var level = response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning;
                logger.Log(level, "T212 {Method} {Path} returned {StatusCode}: {Body}",
                    request.Method, path, (int)response.StatusCode, Truncate(body));

                // Content can only be read once - replace it so Refit's own
                // deserialization downstream still sees the same body.
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            return response;
        }

        private static string Truncate(string body) => body.Length > 500 ? body[..500] + "..." : body;
    }
}
