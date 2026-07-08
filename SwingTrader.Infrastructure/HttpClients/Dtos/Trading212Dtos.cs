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

// Matches T212's actual /equity/account/summary response shape - the
// previous version of this DTO (Free/Total/Blocked/Invested/Ppl/Result all
// nested under "cash") didn't match any real field T212 returns, so every
// one of those properties silently deserialized to 0 rather than throwing.
// That's what caused both the GBP/USD "total = 0" false circuit-breaker
// trigger and a small but persistent mismatch against T212's own app,
// confirmed by inspecting the raw logged response body.
public record T212AccountSummaryCash(
    [property: JsonPropertyName("availableToTrade")] decimal AvailableToTrade,
    [property: JsonPropertyName("reservedForOrders")] decimal ReservedForOrders,
    [property: JsonPropertyName("inPies")] decimal InPies
);

public record T212AccountSummaryInvestments(
    [property: JsonPropertyName("currentValue")] decimal CurrentValue,
    [property: JsonPropertyName("totalCost")] decimal TotalCost,
    [property: JsonPropertyName("realizedProfitLoss")] decimal RealizedProfitLoss,
    [property: JsonPropertyName("unrealizedProfitLoss")] decimal UnrealizedProfitLoss
);

public record T212AccountSummary(
    [property: JsonPropertyName("totalValue")] decimal TotalValue,
    [property: JsonPropertyName("cash")] T212AccountSummaryCash Cash,
    [property: JsonPropertyName("investments")] T212AccountSummaryInvestments Investments
);

public record T212AccountInfo(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode
);

// GET /equity/orders/{id} (OrderResponse above) only returns currently-working
// orders — a market order fills within milliseconds and then 404s on that
// endpoint ("Order not found"), confirmed via live traces where every single
// pending order lookup 404'd, including ones placed under half an hour
// earlier. /equity/history/orders is T212's actual source of truth for a
// filled order's real price - this is what fill reconciliation should poll.
public record HistoricalOrderDetail(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("filledQuantity")] decimal? FilledQuantity,
    [property: JsonPropertyName("filledValue")] decimal? FilledValue
);

// quantity is negative (a deduction) - confirmed live as e.g.
// {"name":"CURRENCY_CONVERSION_FEE","quantity":-0.15,"currency":"GBP"} on
// both a buy and a sell fill. This is the actual fee T212 charged, distinct
// from FX-rate drift between the two legs - subtracting Real Money Exit
// from Real Money Entry would conflate the two.
public record HistoricalFillTax(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("quantity")] decimal Quantity
);

// netValue is the real GBP cash flow for the fill (present on both buys and
// sells, always positive - what actually left/entered the account after FX
// conversion). realisedProfitLoss only appears on sells - T212's own P&L
// figure for the closed leg, confirmed via live traces to be present
// (+1.26 for a real PLTR sell) alongside netValue on the same fill.
public record HistoricalFillWalletImpact(
    [property: JsonPropertyName("netValue")] decimal NetValue,
    [property: JsonPropertyName("realisedProfitLoss")] decimal? RealisedProfitLoss,
    [property: JsonPropertyName("taxes")] List<HistoricalFillTax>? Taxes
);

public record HistoricalFillDetail(
    [property: JsonPropertyName("filledAt")] DateTime FilledAt,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("walletImpact")] HistoricalFillWalletImpact? WalletImpact
);

public record HistoricalOrderItem(
    [property: JsonPropertyName("order")] HistoricalOrderDetail Order,
    [property: JsonPropertyName("fill")] HistoricalFillDetail? Fill
);

public record HistoricalOrdersResponse(
    [property: JsonPropertyName("items")] List<HistoricalOrderItem> Items,
    [property: JsonPropertyName("nextPagePath")] string? NextPagePath
);
