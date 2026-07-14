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
    IJobLogRepository jobLog,
    INotificationRecipientRepository recipients,
    IEmailService emailService,
    IMemoryCache cache,
    IOptions<ExecutionConfig> executionConfig,
    ILogger<PositionExitService> logger) : IPositionExitService
{
    private readonly ExecutionConfig _execution = executionConfig.Value;
    private static readonly TimeZoneInfo EasternTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

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

        // Sell the EXACT instrument that was bought: BrokerTicker was captured
        // at placement precisely because symbol-based resolution can land on a
        // different listing of the same company (see MonitorService's
        // TickerMatchesTrade note) - re-resolving here risked selling an
        // instrument the account doesn't hold while the real position stayed
        // open. The heuristic lookup remains only for legacy trades placed
        // before BrokerTicker was captured.
        string? ticker = trade.BrokerTicker;
        if (string.IsNullOrEmpty(ticker))
        {
            try
            {
                ticker = await ResolveT212TickerAsync(accountId, t212, trade.Symbol);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not resolve T212 ticker for {Symbol} (account {AccountId}) — {Label} deferred to next cycle", trade.Symbol, accountId, label);
                return new PositionExitResult(false, ex.Message, null, null);
            }
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
            await ReenqueueExecutionIfDoneForTodayAsync(accountId, ct);

            return new PositionExitResult(true, null, currentPrice, realizedPnl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Label} order failed for {Symbol} ({Ticker}), account {AccountId} — will retry next cycle", label, trade.Symbol, ticker, accountId);
            return new PositionExitResult(false, ex.Message, null, null);
        }
    }

    // A sell frees up capital - if today's Execution job already ran and
    // completed (or gave up as Failed) before this exit happened, that
    // capital would otherwise sit unused until tomorrow's run rather than
    // funding an approved Buy signal the same day. Deleting today's JobLog
    // row makes SchedulerFunction.TryEnqueueAsync treat Execution as
    // not-yet-run, so its next 5-minute tick (while still inside Execution's
    // 9:20-15:55 ET window) re-enqueues it - the same re-enqueue mechanism
    // already used for late trade approvals. Left alone if Execution is
    // still Enqueued/Processing, to avoid a race where the in-flight run's
    // own MarkCompletedAsync no-ops against a deleted row and a second run
    // gets enqueued alongside it.
    private async Task ReenqueueExecutionIfDoneForTodayAsync(int accountId, CancellationToken ct)
    {
        try
        {
            var todayEt = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTimeZone));
            var existing = await jobLog.FindAsync(accountId, "Execution", todayEt, ct);
            if (existing is { Status: JobStatus.Completed or JobStatus.Failed })
            {
                await jobLog.DeleteAsync(accountId, "Execution", todayEt, ct);
                logger.LogInformation(
                    "Freed capital for account {AccountId} after a same-day exit - Execution will re-run on the next scheduler tick",
                    accountId);
            }
        }
        catch (Exception ex)
        {
            // Not worth failing the exit over - Execution will still run on
            // its normal schedule tomorrow regardless.
            logger.LogWarning(ex, "Failed to re-enqueue Execution for account {AccountId} after exit", accountId);
        }
    }

    private static string ExitReasonLabel(ExitReason reason) => reason switch
    {
        ExitReason.StopLossHit => "Stop loss exit",
        ExitReason.TargetHit => "Target hit exit",
        ExitReason.TrailingStopHit => "Trailing stop exit",
        ExitReason.TimeExit => "Time exit",
        ExitReason.MomentumHealthExit => "Momentum health exit",
        ExitReason.ManualClose => "Manual early exit",
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
            // Exit price and the estimated P&L are in the instrument's own
            // currency (USD quotes) - labelling them £ overstated/understated
            // by the FX rate. The authoritative GBP P&L arrives later via
            // fill reconciliation (T212's realisedProfitLoss).
            var markdown =
                $"# \U0001F4C9 {label} — {trade.Symbol}\n\n" +
                $"Position closed automatically by Acme Trading — no action needed in Trading212.\n\n" +
                $"**Exit price:** ${exitPrice:F2}\n" +
                $"**Estimated P&L:** {pnlSign}${realizedPnl:F2} (confirmed £ figure follows T212's fill)\n" +
                $"**Reason:** {reasonDetail}";

            await emailService.SendSimpleEmailAsync(toAddresses, markdown, $"Acme Trading — {trade.Symbol} closed ({label})");
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

        // US listings only (same rule as ExecutionService's buy-side
        // resolution) - this legacy path only serves trades placed before
        // BrokerTicker capture, which were all US buys.
        var ticker = Execution.T212InstrumentResolver.ResolveUsTicker(instruments, symbol);
        cache.Set(cacheKey, ticker, TimeSpan.FromHours(24));
        return ticker;
    }
}
