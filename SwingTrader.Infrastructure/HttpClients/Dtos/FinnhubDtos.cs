using System.Text.Json.Serialization;

namespace SwingTrader.Infrastructure.HttpClients.Dtos;

// Finnhub returns null for any of these decimal fields when a symbol has no trading data
// for the day (illiquid instruments, some indices, brand-new listings) — all nullable so
// deserialization never throws; callers fall back to 0m where a number is required.
public record FinnhubQuoteResponse(
    [property: JsonPropertyName("c")] decimal? CurrentPrice,
    [property: JsonPropertyName("d")] decimal? Change,
    [property: JsonPropertyName("dp")] decimal? PercentChange,
    [property: JsonPropertyName("h")] decimal? High,
    [property: JsonPropertyName("l")] decimal? Low,
    [property: JsonPropertyName("o")] decimal? Open,
    [property: JsonPropertyName("pc")] decimal? PreviousClose,
    [property: JsonPropertyName("t")] long Timestamp
);

public record FinnhubCandlesResponse(
    [property: JsonPropertyName("c")] List<decimal> Close,
    [property: JsonPropertyName("h")] List<decimal> High,
    [property: JsonPropertyName("l")] List<decimal> Low,
    [property: JsonPropertyName("o")] List<decimal> Open,
    [property: JsonPropertyName("s")] string Status,
    [property: JsonPropertyName("t")] List<long> Timestamps,
    [property: JsonPropertyName("v")] List<long> Volume
);

public record FinnhubNewsItem(
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("datetime")] long Datetime,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("url")] string Url
);

public record FinnhubEarningsEvent(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("hour")] string? Hour,
    [property: JsonPropertyName("epsEstimate")] decimal? EpsEstimate,
    [property: JsonPropertyName("epsActual")] decimal? EpsActual,
    [property: JsonPropertyName("surprisePercent")] decimal? SurprisePercent
);

public record FinnhubEarningsCalendarResponse(
    [property: JsonPropertyName("earningsCalendar")] List<FinnhubEarningsEvent> EarningsCalendar
);

public record MarketMoverItem(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("last")] decimal Price,
    // Not part of the wire payload. Without JsonIgnore the web-defaults
    // naming policy mapped this to "change", COLLIDING with ChangePercent's
    // explicit mapping - System.Text.Json throws on the collision, so every
    // top-movers deserialization failed and screening always saw 0 movers
    // (found 20 Jul 2026 via the qualitative-refresh failure breadcrumb).
    [property: JsonIgnore] decimal Change,
    [property: JsonPropertyName("change")] decimal ChangePercent,
    [property: JsonPropertyName("volume")] long Volume
);

public record AnalystRecommendation(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("strongBuy")] int StrongBuy,
    [property: JsonPropertyName("buy")] int Buy,
    [property: JsonPropertyName("hold")] int Hold,
    [property: JsonPropertyName("sell")] int Sell,
    [property: JsonPropertyName("strongSell")] int StrongSell
);

public record InsiderTransaction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("share")] long Share,
    [property: JsonPropertyName("change")] long Change,
    [property: JsonPropertyName("filingDate")] string FilingDate,
    [property: JsonPropertyName("transactionDate")] string TransactionDate,
    [property: JsonPropertyName("transactionCode")] string TransactionCode
);

public record InsiderTransactionsResponse(
    [property: JsonPropertyName("data")] List<InsiderTransaction> Data,
    [property: JsonPropertyName("symbol")] string Symbol
);

// One month of Finnhub's aggregated insider sentiment. Mspr (Monthly Share
// Purchase Ratio) ranges -100 (pure selling) to +100 (pure buying).
public record InsiderSentimentEntry(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("change")] long Change,
    [property: JsonPropertyName("mspr")] decimal Mspr
);

public record InsiderSentimentResponse(
    [property: JsonPropertyName("data")] List<InsiderSentimentEntry> Data,
    [property: JsonPropertyName("symbol")] string Symbol
);

public record RevenueEstimate(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("revenueAvg")] decimal RevenueAvg,
    [property: JsonPropertyName("numberOfAnalysts")] int NumberOfAnalysts
);

public record RevenueEstimateResponse(
    [property: JsonPropertyName("data")] List<RevenueEstimate> Data,
    [property: JsonPropertyName("symbol")] string Symbol
);


public record FinnhubCompanyProfileResponse(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("ticker")] string? Ticker,
    [property: JsonPropertyName("finnhubIndustry")] string? Industry
);
