using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Monitor;

public class PositionExitService(
    ITradeRepository tradeRepo,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    IMemoryCache cache,
    IOptions<ExecutionConfig> executionConfig,
    ILogger<PositionExitService> logger) : IPositionExitService
{
    private readonly ExecutionConfig _execution = executionConfig.Value;

    public async Task<PositionExitResult> ClosePositionAsync(
        int accountId,
        Trade trade,
        ITrading212Client t212,
        decimal currentPrice,
        ExitReason exitReason,
        string reasonDetail,
        CancellationToken ct = default)
    {
        var label = ExitReasonLabel(exitReason);

        string? ticker;
        try
        {
            ticker = await ResolveT212TickerAsync(accountId, t212, trade.Symbol);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve T212 ticker for {Symbol} (account {AccountId}) — {Label} deferred to next cycle", trade.Symbol, accountId, label);
            return new PositionExitResult(false, ex.Message, null, null);
        }

        if (ticker is null)
        {
            logger.LogWarning("No T212 instrument found for {Symbol} (account {AccountId}) — {Label} deferred to next cycle", trade.Symbol, accountId, label);
            return new PositionExitResult(false, "No matching T212 instrument found", null, null);
        }

        try
        {
            // MonitorService already called t212.GetAccountSummaryAsync() at the top
            // of this cycle (circuit breaker check), and ResolveT212TickerAsync above
            // may have just called GetInstrumentsAsync() too — space this write call
            // out from those reads so a run with multiple exits doesn't stack T212
            // calls back-to-back into the same rate-limit bucket. Same delay
            // ExecutionService uses between order placements.
            await Task.Delay(TimeSpan.FromSeconds(_execution.DelayBetweenOrdersSeconds), ct);

            // T212's market order endpoint is direction-agnostic — a negative
            // quantity sells. This is the only place in the app that does so.
            var order = await t212.PlaceMarketOrderAsync(new MarketOrderRequest(ticker, -trade.Quantity));

            logger.LogInformation(
                "{Label} executed for account {AccountId}: {Symbol} ({Ticker}) qty={Qty} orderId={OrderId} reason={Reason}",
                label, accountId, trade.Symbol, ticker, trade.Quantity, order.Id, reasonDetail);

            var realizedPnl = (currentPrice - trade.EntryPrice) * trade.Quantity;

            trade.Status = TradeStatus.Closed;
            trade.ExitPrice = currentPrice;
            trade.ClosedAt = DateTime.UtcNow;
            trade.ExitOrderId = order.Id.ToString();
            trade.RealizedPnl = realizedPnl;
            trade.Notes = (trade.Notes ?? string.Empty).TrimEnd() + $" | {exitReason}: {reasonDetail}";
            await tradeRepo.UpdateAsync(trade);

            await SendExitNotificationAsync(accountId, trade, currentPrice, realizedPnl, label, reasonDetail);

            return new PositionExitResult(true, null, currentPrice, realizedPnl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Label} order failed for {Symbol} ({Ticker}), account {AccountId} — will retry next cycle", label, trade.Symbol, ticker, accountId);
            return new PositionExitResult(false, ex.Message, null, null);
        }
    }

    private static string ExitReasonLabel(ExitReason reason) => reason switch
    {
        ExitReason.StopLossHit => "Stop loss exit",
        ExitReason.TargetHit => "Target hit exit",
        ExitReason.TrailingStopHit => "Trailing stop exit",
        ExitReason.TimeExit => "Time exit",
        ExitReason.MomentumHealthExit => "Momentum health exit",
        _ => "Exit",
    };

    private async Task SendExitNotificationAsync(int accountId, Trade trade, decimal exitPrice, decimal realizedPnl, string label, string reasonDetail)
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
                $"# \U0001F4C9 {label} — {trade.Symbol}\n\n" +
                $"Position closed automatically by SwingTrader — no action needed in Trading212.\n\n" +
                $"**Exit price:** £{exitPrice:F2}\n" +
                $"**P&L:** {pnlSign}£{realizedPnl:F2}\n" +
                $"**Reason:** {reasonDetail}";

            await emailService.SendSimpleEmailAsync(toAddresses, markdown, $"SwingTrader — {trade.Symbol} closed ({label})");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send exit notification email for account {AccountId}", accountId);
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
