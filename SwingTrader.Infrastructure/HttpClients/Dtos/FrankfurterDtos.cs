using System.Text.Json.Serialization;

namespace SwingTrader.Infrastructure.HttpClients.Dtos;

public record FrankfurterRatesResponse(
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("rates")] Dictionary<string, decimal> Rates
);
