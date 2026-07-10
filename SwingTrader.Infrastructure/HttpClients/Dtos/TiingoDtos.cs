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

// One bar from Tiingo's IEX intraday endpoint (/iex/{ticker}/prices).
// Shape captured from a real Power-plan response on 2026-07-10:
//   {"date":"2026-07-10T13:30:00.000Z","open":314.72,"high":315.565,
//    "low":313.23,"close":315.48,"volume":43916.0}
// Dates are UTC ISO-8601; volume arrives only when requested via the columns
// parameter and can be null on sparse bars, so it's nullable here.
// One article from Tiingo's news endpoint (/tiingo/news). Shape captured from
// a real Power-plan response on 2026-07-10:
//   {"id":97126804,"publishedDate":"2026-07-10T21:30:30.430492Z",
//    "title":"...","url":"https://...","description":null,
//    "source":"gurufocus.com","tags":["Stock",...],
//    "crawlDate":"...","tickers":["aapl","aapl"]}
// description is often null; tickers can contain duplicates; there is NO
// sentiment field on any plan - the scoring is ours.
public record TiingoNewsItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("publishedDate")] DateTime PublishedDate,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("source")] string? Source
);

public record TiingoIexPrice(
    [property: JsonPropertyName("date")] DateTime Date,
    [property: JsonPropertyName("open")] decimal? Open,
    [property: JsonPropertyName("high")] decimal? High,
    [property: JsonPropertyName("low")] decimal? Low,
    [property: JsonPropertyName("close")] decimal? Close,
    [property: JsonPropertyName("volume")] decimal? Volume
);
