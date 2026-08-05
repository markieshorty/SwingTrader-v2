using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Agents.Execution;

public interface ISpyCoreService
{
    // Takes the monitor cycle's already-fetched account summary: refetching
    // it here tripped T212's per-endpoint rate limit (429 on
    // /equity/account/summary, seen 5 Aug 2026) and killed the core check
    // on every cycle.
    Task RunAsync(Account account, ITrading212Client t212, T212AccountSummary summary, CancellationToken ct = default);
}

// SPY-core sleeve manager (docs/sleeves-plan P1): keeps the passive core at
// its target share of account equity with BAND rebalancing - orders only
// when drift exceeds 5% of the sleeve target (a few orders a year, not a
// tracker). Runs on the monitor cycle during market hours; entirely inactive
// while SpyCorePct is 0 and no core position exists.
public class SpyCoreService(
    IAccountAllocationRepository allocations,
    ITradeRepository trades,
    IActivityLogRepository activityLog,
    ILogger<SpyCoreService> logger) : ISpyCoreService
{
    private const decimal DriftBandFraction = 0.05m;
    private const decimal MinOrderGbp = 25m;

    // Pure band maths, unit-tested: the GBP delta to trade, or null inside
    // the band / below the order floor.
    internal static decimal? RebalanceDelta(decimal targetValue, decimal currentValue)
    {
        if (targetValue <= 0 && currentValue <= 0) return null;
        var delta = targetValue - currentValue;
        var band = Math.Max(targetValue * DriftBandFraction, MinOrderGbp);
        return Math.Abs(delta) < band ? null : delta;
    }

    public async Task RunAsync(Account account, ITrading212Client t212, T212AccountSummary summary, CancellationToken ct = default)
    {
        var alloc = await allocations.GetAsync(account.Id, ct);
        var coreTrade = (await trades.GetOpenTradesAsync(account.Id, account.TradingMode))
            .FirstOrDefault(t => t.Sleeve == SleeveType.SpyCore);
        if (alloc.SpyCorePct <= 0 && coreTrade is null) return; // sleeve off, nothing held

        // Entry pause governs new buying, never selling-down.
        var paused = account.IsExecutionPaused(account.TradingMode);

        var target = summary.TotalValue * alloc.SpyCorePct;

        // Price + current value from the broker's own portfolio when held -
        // the one source that works for any listed instrument. A first buy
        // needs a price we may not have (UCITS ETFs aren't on Finnhub free) -
        // in that case an activity note asks for a one-off manual seed buy;
        // once any core position exists the manager takes over.
        var portfolio = await t212.GetPortfolioAsync();
        var position = portfolio.FirstOrDefault(p =>
            p.Ticker.StartsWith(alloc.CoreTicker, StringComparison.OrdinalIgnoreCase));

        var currentValue = position is null ? 0m : position.Quantity * position.CurrentPrice;
        var delta = RebalanceDelta(target, currentValue);
        if (delta is null) return;

        if (delta > 0 && paused)
        {
            logger.LogInformation("SPY core top-up skipped for account {AccountId} — entries paused", account.Id);
            return;
        }

        if (position is null)
        {
            // No price available for a first buy - one-off manual seed.
            if (coreTrade is null)
                await activityLog.LogAsync(account.Id, "SystemEvent", "SPY Core Needs Seeding", "Warning",
                    $"The core sleeve targets £{target:N0} of {alloc.CoreTicker} but no position exists and no price " +
                    $"is available to size a first order. Buy any amount of {alloc.CoreTicker} manually in Trading 212 " +
                    "once — the sleeve manager maintains the band from then on.");
            return;
        }

        var price = position.CurrentPrice;
        if (price <= 0) return;
        var quantity = Math.Floor(Math.Abs(delta.Value) / price * 1000m) / 1000m;
        if (quantity <= 0) return;
        if (delta < 0) quantity = -Math.Min(quantity, position.Quantity);

        // Buys leave the standard ~6% market-order headroom on cash.
        if (delta > 0)
        {
            var cash = summary.Cash.AvailableToTrade / 1.06m;
            if (quantity * price > cash)
                quantity = Math.Floor(cash / price * 1000m) / 1000m;
            if (quantity <= 0) return;
        }

        var order = await t212.PlaceMarketOrderAsync(new MarketOrderRequest(position.Ticker, quantity));
        var action = quantity > 0 ? "topped up" : "trimmed";
        logger.LogInformation("SPY core {Action} for account {AccountId}: {Qty} {Ticker} (~£{Value:N0})",
            action, account.Id, quantity, position.Ticker, Math.Abs(quantity) * price);

        // Ledger: one long-lived Trade row per core position, stamped
        // Sleeve=SpyCore so it is invisible to swing slots, exits and stats.
        if (coreTrade is null)
        {
            await trades.AddAsync(new Trade
            {
                AccountId = account.Id,
                TradingMode = account.TradingMode,
                Symbol = alloc.CoreTicker.ToUpperInvariant(),
                BrokerTicker = position.Ticker,
                Direction = TradeDirection.Long,
                Sleeve = SleeveType.SpyCore,
                EntryPrice = price,
                Quantity = Math.Max(quantity, 0m) + position.Quantity,
                EntryOrderId = order.Id.ToString(),
                Status = TradeStatus.Open,
                OpenedAt = DateTime.UtcNow,
                StopLossPrice = 0m,
                TargetPrice = 0m,
                Notes = $"SPY core sleeve ({alloc.SpyCorePct:P0} target). {action} ~£{Math.Abs(quantity) * price:N0}.",
            });
        }
        else
        {
            coreTrade.Quantity = Math.Max(0m, position.Quantity + quantity);
            coreTrade.Notes = $"{coreTrade.Notes} | {DateTime.UtcNow:MM-dd} {action} ~£{Math.Abs(quantity) * price:N0}";
            if (coreTrade.Quantity <= 0)
            {
                coreTrade.Status = TradeStatus.Closed;
                coreTrade.ClosedAt = DateTime.UtcNow;
                coreTrade.ExitPrice = price;
            }
            await trades.UpdateAsync(coreTrade);
        }

        await activityLog.LogAsync(account.Id, "SystemEvent", "SPY Core Rebalanced", "Info",
            $"Core sleeve {action}: {Math.Abs(quantity):0.###} {position.Ticker} (~£{Math.Abs(quantity) * price:N0}) " +
            $"— now ~£{currentValue + quantity * price:N0} vs target £{target:N0}.", ct);
    }
}
