using System.Text.Json.Serialization;

namespace SwingTrader.Infrastructure.HttpClients.Dtos;

public record TiingoDailyPrice(
    [property: JsonPropertyName("date")] DateTime Date,
    [property: JsonPropertyName("open")] decimal Open,
    [property: JsonPropertyName("high")] decimal High,
    [property: JsonPropertyName("low")] decimal Low,
    [property: JsonPropertyName("close")] decimal Close,
    [property: JsonPropertyName("volume")] decimal Volume,
    [property: JsonPropertyName("adjClose")] decimal AdjClose,
    [property: JsonPropertyName("adjOpen")] decimal AdjOpen,
    [property: JsonPropertyName("adjHigh")] decimal AdjHigh,
    [property: JsonPropertyName("adjLow")] decimal AdjLow,
    [property: JsonPropertyName("adjVolume")] decimal AdjVolume
);
