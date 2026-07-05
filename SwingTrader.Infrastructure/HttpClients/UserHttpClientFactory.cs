using Refit;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.HttpClients;

public class UserHttpClientFactory(IUserKeyService keys, IAccountRepository accounts) : IUserHttpClientFactory
{
    private const string FinnhubBaseUrl = "https://finnhub.io/api/v1";
    private const string TiingoBaseUrl = "https://api.tiingo.com";
    private const string ClaudeBaseUrl = "https://api.anthropic.com";
    private const string Trading212DemoBaseUrl = "https://demo.trading212.com";
    private const string Trading212LiveBaseUrl = "https://live.trading212.com";

    public async Task<TClient> CreateFinnhubAsync<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await keys.GetKeyAsync(accountId, ApiKeyProviders.Finnhub, ct);
        // Finnhub authenticates via a "token" query parameter rather than a
        // header, so each request is signed by this handler instead of a
        // fixed default header.
        var handler = new FinnhubTokenHandler(apiKey) { InnerHandler = new HttpClientHandler() };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(FinnhubBaseUrl) };
        return RestService.For<TClient>(httpClient);
    }

    public async Task<TClient> CreateTiingoAsync<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await keys.GetKeyAsync(accountId, ApiKeyProviders.Tiingo, ct);
        var httpClient = new HttpClient { BaseAddress = new Uri(TiingoBaseUrl) };
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
        return RestService.For<TClient>(httpClient);
    }

    public async Task<TClient> CreateTrading212Async<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await keys.GetKeyAsync(accountId, ApiKeyProviders.Trading212Key, ct);
        var apiSecret = await keys.GetKeyAsync(accountId, ApiKeyProviders.Trading212Secret, ct);
        var account = await accounts.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        var baseUrl = account.TradingMode == TradingMode.Live ? Trading212LiveBaseUrl : Trading212DemoBaseUrl;
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");
        return RestService.For<TClient>(httpClient);
    }

    public async Task<TClient> CreateClaudeAsync<TClient>(int accountId, CancellationToken ct = default)
    {
        var apiKey = await keys.GetKeyAsync(accountId, ApiKeyProviders.Claude, ct);
        var httpClient = new HttpClient { BaseAddress = new Uri(ClaudeBaseUrl) };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return RestService.For<TClient>(httpClient);
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
}
