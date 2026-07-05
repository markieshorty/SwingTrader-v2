using Refit;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.HttpClients;

/// <summary>
/// Frankfurter (ECB reference rates) — free, keyless FX rate API. Used for the
/// GBP/USD display-conversion rate since Finnhub's forex data (both the
/// dedicated /forex/rates endpoint and forex quotes via /quote) returned 403
/// on this account's plan.
/// </summary>
public interface IExchangeRateClient
{
    [Get("/v1/latest")]
    Task<FrankfurterRatesResponse> GetLatestRatesAsync(
        [AliasAs("base")] string @base, [AliasAs("symbols")] string symbols);
}
