using Refit;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.HttpClients;

public interface ITrading212Client
{
    [Get("/api/v0/equity/account/cash")]
    Task<AccountCashResponse> GetAccountCashAsync();

    [Get("/api/v0/equity/portfolio")]
    Task<List<PortfolioPositionResponse>> GetPortfolioAsync();

    [Post("/api/v0/equity/orders/market")]
    Task<OrderResponse> PlaceMarketOrderAsync([Body] MarketOrderRequest request);

    [Get("/api/v0/equity/orders/{orderId}")]
    Task<OrderResponse> GetOrderAsync(string orderId);

    [Delete("/api/v0/equity/orders/{orderId}")]
    Task CancelOrderAsync(string orderId);

    [Get("/api/v0/equity/metadata/instruments")]
    Task<List<InstrumentResponse>> GetInstrumentsAsync();

    [Get("/api/v0/equity/account/summary")]
    Task<T212AccountSummary> GetAccountSummaryAsync();

    [Get("/api/v0/equity/account/info")]
    Task<T212AccountInfo> GetAccountInfoAsync();
}
