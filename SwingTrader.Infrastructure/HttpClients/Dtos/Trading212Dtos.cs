using System.Text.Json.Serialization;

namespace SwingTrader.Infrastructure.HttpClients.Dtos;

public record AccountCashResponse(
    [property: JsonPropertyName("free")] decimal Free,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("blocked")] decimal Blocked,
    [property: JsonPropertyName("invested")] decimal Invested,
    [property: JsonPropertyName("ppl")] decimal Ppl,
    [property: JsonPropertyName("result")] decimal Result,
    [property: JsonPropertyName("pieCash")] decimal PieCash
);

public record PortfolioPositionResponse(
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("averagePrice")] decimal AveragePrice,
    [property: JsonPropertyName("currentPrice")] decimal CurrentPrice,
    [property: JsonPropertyName("ppl")] decimal Ppl,
    [property: JsonPropertyName("fxPpl")] decimal? FxPpl,
    [property: JsonPropertyName("initialFillDate")] string? InitialFillDate,
    [property: JsonPropertyName("frontend")] string? Frontend,
    [property: JsonPropertyName("maxBuy")] decimal? MaxBuy,
    [property: JsonPropertyName("maxSell")] decimal? MaxSell,
    [property: JsonPropertyName("pieQuantity")] decimal? PieQuantity
);

// T212 /equity/portfolio returns a bare JSON array — no wrapper object.
// GetPortfolioAsync returns List<PortfolioPositionResponse> directly.

public record MarketOrderRequest(
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("quantity")] decimal Quantity
);

public record OrderResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("filledQuantity")] decimal? FilledQuantity,
    [property: JsonPropertyName("fillPrice")] decimal? FillPrice,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("fillResult")] string? FillResult,
    [property: JsonPropertyName("creationTime")] string CreationTime,
    [property: JsonPropertyName("fillTime")] string? FillTime
);

public record InstrumentResponse(
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("isin")] string Isin
);

public record T212AccountSummaryCash(
    [property: JsonPropertyName("free")] decimal Free,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("blocked")] decimal Blocked,
    [property: JsonPropertyName("invested")] decimal Invested,
    [property: JsonPropertyName("ppl")] decimal Ppl,
    [property: JsonPropertyName("result")] decimal Result,
    [property: JsonPropertyName("availableToTrade")] decimal AvailableToTrade
);

public record T212AccountSummary(
    [property: JsonPropertyName("cash")] T212AccountSummaryCash Cash
);

public record T212AccountInfo(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode
);
