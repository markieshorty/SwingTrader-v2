using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Monitor;

public class PositionExitService(
    ITradeRepository tradeRepo,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    IMemoryCache cache,
    ILogger<PositionExitService> logger) : IPositionExitService
{
    public async Task<PositionExitResult> ClosePositionAsync(
        int accountId,
        Trade trade,
        ITrading212Client t212,
        decimal currentPrice,
        string reason,
        CancellationToken ct = default)
    {
        string? ticker;
        try
        {
            ticker = await ResolveT212TickerAsync(accountId, t212, trade.Symbol);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve T212 ticker for {Symbol} (account {AccountId}) — momentum exit deferred to next cycle", trade.Symbol, accountId);
            return new PositionExitResult(false, ex.Message, null, null);
        }

        if (ticker is null)
        {
            logger.LogWarning("No T212 instrument found for {Symbol} (account {AccountId}) — momentum exit deferred to next cycle", trade.Symbol, accountId);
            return new PositionExitResult(false, "No matching T212 instrument found", null, null);
        }

        try
        {
            // T212's market order endpoint is direction-agnostic — a negative
            // quantity sells. This is the only place in the app that does so.
            var order = await t212.PlaceMarketOrderAsync(new MarketOrderRequest(ticker, -trade.Quantity));

            logger.LogInformation(
                "Momentum health exit executed for account {AccountId}: {Symbol} ({Ticker}) qty={Qty} orderId={OrderId} reason={Reason}",
                accountId, trade.Symbol, ticker, trade.Quantity, order.Id, reason);

            var realizedPnl = (currentPrice - trade.EntryPrice) * trade.Quantity;

            trade.Status = TradeStatus.Closed;
            trade.ExitPrice = currentPrice;
            trade.ClosedAt = DateTime.UtcNow;
            trade.ExitOrderId = order.Id.ToString();
            trade.RealizedPnl = realizedPnl;
            trade.Notes = (trade.Notes ?? string.Empty).TrimEnd() + $" | MomentumHealthExit: {reason}";
            await tradeRepo.UpdateAsync(trade);

            await SendExitNotificationAsync(accountId, trade, currentPrice, realizedPnl, reason);

            return new PositionExitResult(true, null, currentPrice, realizedPnl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Momentum health exit order failed for {Symbol} ({Ticker}), account {AccountId} — will retry next cycle", trade.Symbol, ticker, accountId);
            return new PositionExitResult(false, ex.Message, null, null);
        }
    }

    private async Task SendExitNotificationAsync(int accountId, Trade trade, decimal exitPrice, decimal realizedPnl, string reason)
    {
        try
        {
            var toAddresses = (await recipients.ListAsync(accountId))
                .Where(r => r.Categories.HasFlag(NotificationCategory.Execution))
                .Select(r => r.Email)
                .ToList();

            if (toAddresses.Count == 0) return;

            var pnlSign = realizedPnl >= 0 ? "+" : "";
            var markdown =
                $"# \U0001F4C9 Momentum health exit — {trade.Symbol}\n\n" +
                $"Position closed automatically: momentum failed to confirm during the probation period.\n\n" +
                $"**Exit price:** £{exitPrice:F2}\n" +
                $"**P&L:** {pnlSign}£{realizedPnl:F2}\n" +
                $"**Reason:** {reason}";

            await emailService.SendSimpleEmailAsync(toAddresses, markdown, $"SwingTrader — {trade.Symbol} closed (momentum health exit)");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send momentum exit notification email for account {AccountId}", accountId);
        }
    }

    private async Task<string?> ResolveT212TickerAsync(int accountId, ITrading212Client t212, string symbol)
    {
        var cacheKey = $"t212_ticker_{accountId}_{symbol}";
        if (cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        var instrumentsCacheKey = $"t212_instruments_all_{accountId}";
        List<InstrumentResponse> instruments;
        if (cache.TryGetValue(instrumentsCacheKey, out List<InstrumentResponse>? all) && all is not null)
        {
            instruments = all;
        }
        else
        {
            instruments = await t212.GetInstrumentsAsync();
            cache.Set(instrumentsCacheKey, instruments, TimeSpan.FromHours(24));
        }

        var match = instruments.FirstOrDefault(i =>
            i.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase)
            || i.Ticker.StartsWith(symbol + "_", StringComparison.OrdinalIgnoreCase)
            || i.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        var ticker = match?.Ticker;
        cache.Set(cacheKey, ticker, TimeSpan.FromHours(24));
        return ticker;
    }
}
