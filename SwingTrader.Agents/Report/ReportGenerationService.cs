using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Core.Trading;

namespace SwingTrader.Agents.Report;

public class ReportGenerationService(
    ISignalRepository signalRepo,
    ITradeRepository tradeRepo,
    IPortfolioRepository portfolioRepo,
    IReportRepository reportRepo,
    IApprovalRepository approvalRepo,
    IAccountRepository accountRepo,
    IAccountRiskProfileRepository riskProfileRepo,
    IForexService forex,
    IMarketCalendarService marketCalendar,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<ClaudeConfig> claudeConfig,
    IOptions<ReportConfig> reportConfig,
    IOptions<ApprovalConfig> approvalConfig,
    ILogger<ReportGenerationService> logger) : IReportGenerationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<DailyReport> GenerateAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITrading212Client t212,
        IClaudeClient claude,
        DateOnly reportDate,
        CancellationToken ct = default)
    {
        // Fetched up front (moved ahead of its original Step 6 spot) since
        // Step 2's portfolio load also needs TradingMode, to keep Demo/Live
        // snapshot baselines from mixing (see PortfolioSnapshot.TradingMode).
        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        // Step 1
        var (buys, watches, holds, avoids, funnel) = await LoadSignalsAsync(accountId, reportDate);

        if (buys.Count == 0 && watches.Count == 0 && holds == 0 && avoids.Count == 0)
        {
            logger.LogWarning("No signals found for {Date} (account {AccountId}) — research may not have completed", reportDate, accountId);
            var empty = new DailyReport
            {
                AccountId = accountId,
                TradingMode = account.TradingMode,
                ReportDate = reportDate,
                ReportMarkdown = $"# ⚠️ No signals available for {reportDate}\n\nThe research agent may not have run yet.\nCheck logs for errors.",
                TopBuysJson = "[]",
                TopSellsJson = "[]",
                WasSent = false,
            };
            return await reportRepo.AddAsync(empty);
        }

        // GBP per USD — per-share prices from Finnhub (entry zones, stops, targets,
        // unrealised P&L) are USD; convert for display so the email reads in £ like
        // the T212 app. Cash figures come from T212 account summary (already GBP).
        var gbpUsd = await forex.GetGbpUsdRateAsync(ct);

        // Step 2
        var portfolio = await LoadPortfolioStateAsync(accountId, account.TradingMode, finnhub, t212, ct);

        // Step 3
        var market = await FetchMarketContextAsync(finnhub, ct);

        // Step 4
        var narratives = await GenerateNarrativesAsync(claude, buys, portfolio, market, gbpUsd, ct);

        // Step 5
        await CalculateEntryLevelsAsync(accountId, buys, finnhub, ct);

        // Step 6 - ApprovalRequired is a per-account setting (Settings page),
        // not a global one - ApprovalConfig now only carries the base URL and
        // approval-window timing, both genuinely environment-level.
        var approvalRequired = account.ApprovalRequired;
        var cfg = reportConfig.Value;
        var markdown = BuildMarkdown(reportDate, buys, watches, holds, avoids, portfolio, market, narratives, cfg, gbpUsd, funnel);
        if (approvalRequired)
        {
            var baseUrl = approvalConfig.Value.BaseUrl.TrimEnd('/');
            markdown += $"\n\n---\n⚠️ **Approval required** — visit [{baseUrl}/trades?tab=approvals]({baseUrl}/trades?tab=approvals) to approve today's trades before they execute.";
        }
        var approvalMarkdown = approvalRequired ? BuildApprovalMarkdown(reportDate) : null;

        // Step 7
        return await PersistAsync(accountId, account.TradingMode, reportDate, markdown, approvalMarkdown, buys, portfolio, narratives.MarketContext, approvalRequired);
    }

    // ── Step 1 ───────────────────────────────────────────────────────────────

    // Funnel shadow (Phase F1): what the two-stage design WOULD have decided
    // today vs what the legacy blend actually decided - the divergence
    // evidence the F2 flip is gated on.
    internal sealed record FunnelShadowStats(
        int Scored, int LegacyBuys, int GateWouldBuy, List<string> DivergentSymbols, int WouldVeto);

    internal static FunnelShadowStats ComputeFunnelShadowStats(IReadOnlyList<StockSignal> all)
    {
        var scored = all.Where(s => s.GateScore is not null).ToList();
        var divergent = scored
            .Where(s => (s.Recommendation == Recommendation.Buy) != s.WouldPassGate)
            .Select(s => $"{s.Symbol}{(s.WouldPassGate ? "+" : "-")}") // + gate-only, - legacy-only
            .OrderBy(s => s)
            .ToList();
        return new FunnelShadowStats(
            scored.Count,
            scored.Count(s => s.Recommendation == Recommendation.Buy),
            scored.Count(s => s.WouldPassGate),
            divergent,
            scored.Count(s => s.WouldBeVetoed));
    }

    private async Task<(List<StockSignal> buys, List<StockSignal> watches, int holds, List<StockSignal> avoids, FunnelShadowStats funnel)>
        LoadSignalsAsync(int accountId, DateOnly date)
    {
        var all = (await signalRepo.GetByDateAsync(accountId, date)).ToList();

        var buys = all.Where(s => s.Recommendation == Recommendation.Buy)
                      .OrderByDescending(s => s.ConvictionScore)
                      .ToList();

        var watches = all.Where(s => s.Recommendation == Recommendation.Watch)
                         .OrderByDescending(s => s.ConvictionScore)
                         .Take(reportConfig.Value.MaxWatchesInReport)
                         .ToList();

        var holds = all.Count(s => s.Recommendation == Recommendation.Hold);

        var avoids = all.Where(s => s.Recommendation == Recommendation.Avoid)
                        .ToList();

        var funnel = ComputeFunnelShadowStats(all);

        logger.LogInformation("Signals loaded: {Buys} buys, {Watches} watches, {Holds} holds, {Avoids} avoids",
            buys.Count, watches.Count, holds, avoids.Count);

        return (buys, watches, holds, avoids, funnel);
    }

    // ── Step 2 ───────────────────────────────────────────────────────────────

    private async Task<PortfolioState> LoadPortfolioStateAsync(
        int accountId, TradingMode tradingMode, IFinnhubClient finnhub, ITrading212Client t212, CancellationToken ct)
    {
        var openTrades = (await tradeRepo.GetOpenTradesAsync(accountId, tradingMode)).ToList();
        var positions = new List<OpenPositionState>();

        foreach (var trade in openTrades)
        {
            var currentPrice = trade.EntryPrice;
            try
            {
                var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                if (quote.CurrentPrice > 0) currentPrice = quote.CurrentPrice!.Value;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Quote fetch failed for {Symbol} — using entry price", trade.Symbol);
            }

            var unrealisedAmt = (currentPrice - trade.EntryPrice) * trade.Quantity;
            var unrealisedPct = (currentPrice - trade.EntryPrice) / trade.EntryPrice * 100m;
            // Trading days held (weekends/holidays excluded), matching the
            // time-exit accounting in PositionMonitorService so the reported
            // age and the exit logic never disagree.
            var daysHeld = marketCalendar.TradingDaysBetween(
                DateOnly.FromDateTime(trade.OpenedAt), DateOnly.FromDateTime(DateTime.UtcNow));
            var pctFromStop = (currentPrice - trade.StopLossPrice) / trade.StopLossPrice * 100m;
            var pctFromTarget = (trade.TargetPrice - currentPrice) / trade.TargetPrice * 100m;
            var cfg = reportConfig.Value;
            var isNearStop = (double)pctFromStop < cfg.OpenPositionWarningPctFromStop;
            var isNearTarget = (double)pctFromTarget < cfg.OpenPositionWarningPctFromTarget;
            var isTimeAlert = daysHeld > cfg.TimeExitWarningDays && !isNearTarget;

            positions.Add(new OpenPositionState(
                trade, currentPrice, unrealisedAmt, unrealisedPct,
                daysHeld, pctFromStop, pctFromTarget, isNearStop, isNearTarget, isTimeAlert));
        }

        var snapshot = await portfolioRepo.GetLatestSnapshotAsync(accountId, tradingMode);
        decimal cashAvailable;
        try
        {
            var summary = await t212.GetAccountSummaryAsync();
            cashAvailable = summary.Cash.AvailableToTrade;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "T212 account summary unavailable — falling back to last snapshot");
            cashAvailable = snapshot?.CashAvailable ?? 0m;
        }

        var history = (await tradeRepo.GetTradeHistoryAsync(
            accountId, tradingMode, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow)).ToList();
        var closed = history.Where(t => t.Status != TradeStatus.Open).ToList();

        PerformanceStats stats;
        if (closed.Count == 0)
        {
            stats = new PerformanceStats(0, 0, 0, 0, closed.Count, "Insufficient trade history");
        }
        else
        {
            var winners = closed.Where(t => (t.RealizedPnl ?? 0) > 0).ToList();
            var losers = closed.Where(t => (t.RealizedPnl ?? 0) <= 0).ToList();
            var winRate = (decimal)winners.Count / closed.Count;
            var avgWinPct = winners.Count > 0
                ? winners.Average(t => (t.RealizedPnl ?? 0) / t.EntryPrice * 100m) : 0m;
            var avgLossPct = losers.Count > 0
                ? losers.Average(t => (t.RealizedPnl ?? 0) / t.EntryPrice * 100m) : 0m;
            var ev = winRate * avgWinPct + (1 - winRate) * avgLossPct;
            stats = new PerformanceStats(winRate, avgWinPct, avgLossPct, ev, closed.Count, null);
        }

        return new PortfolioState(positions, snapshot, cashAvailable, stats);
    }

    // ── Step 3 ───────────────────────────────────────────────────────────────

    private async Task<MarketContext> FetchMarketContextAsync(IFinnhubClient finnhub, CancellationToken ct)
    {
        decimal spyPrice = 0, spyChange = 0, spyChangePct = 0;
        decimal qqqPrice = 0, qqqChange = 0, qqqChangePct = 0;
        decimal vix = 0;

        try
        {
            var q = await finnhub.GetQuoteAsync("SPY");
            spyPrice = q.CurrentPrice ?? 0m; spyChange = q.Change ?? 0m; spyChangePct = q.PercentChange ?? 0m;
        }
        catch { logger.LogWarning("SPY quote unavailable"); }

        try
        {
            var q = await finnhub.GetQuoteAsync("QQQ");
            qqqPrice = q.CurrentPrice ?? 0m; qqqChange = q.Change ?? 0m; qqqChangePct = q.PercentChange ?? 0m;
        }
        catch { logger.LogWarning("QQQ quote unavailable"); }

        try
        {
            var q = await finnhub.GetQuoteAsync("VIX");
            vix = q.CurrentPrice ?? 0m;
        }
        catch { logger.LogWarning("VIX quote unavailable"); }

        var vixLabel = vix switch
        {
            > 0 and < 15 => "calm",
            >= 15 and < 20 => "moderate",
            >= 20 and < 25 => "elevated",
            >= 25 => "high — reduce size",
            _ => "unknown"
        };

        var marketLabel = spyChangePct switch
        {
            > 0.5m => "bullish",
            < -0.5m => "bearish",
            _ => "flat"
        };

        var techLabel = qqqChangePct switch
        {
            > 0.5m => "bullish",
            < -0.5m => "bearish",
            _ => "flat"
        };

        return new MarketContext(
            spyPrice, spyChange, spyChangePct,
            qqqPrice, qqqChange, qqqChangePct,
            vix, vixLabel, marketLabel, techLabel);
    }

    // ── Step 4 ───────────────────────────────────────────────────────────────

    private async Task<ReportNarratives> GenerateNarrativesAsync(
        IClaudeClient claude, List<StockSignal> buys, PortfolioState portfolio, MarketContext market, decimal gbpUsd, CancellationToken ct)
    {
        var marketContext = await GenerateMarketNarrativeAsync(claude, market, ct);
        var buyNarratives = new Dictionary<string, string>();

        foreach (var buy in buys.Take(reportConfig.Value.MaxBuysInReport))
            buyNarratives[buy.Symbol] = await GenerateBuyNarrativeAsync(claude, buy, ct);

        var portfolioNarrative = await GeneratePortfolioNarrativeAsync(portfolio, gbpUsd);

        return new ReportNarratives(marketContext, buyNarratives, portfolioNarrative);
    }

    private async Task<string> GenerateMarketNarrativeAsync(IClaudeClient claude, MarketContext m, CancellationToken ct)
    {
        var fallback = $"Markets showing {m.MarketLabel} conditions with VIX at {m.Vix:F1}. " +
                       $"{Capitalize(m.VixLabel)} volatility environment — adjust position sizes accordingly.";
        try
        {
            var system = "Write a 2-sentence pre-market briefing for a swing trader. " +
                         "Plain English. No jargon. No bullet points. No headers. No markdown. " +
                         "Return only the sentences, nothing else.";

            var spyStr = m.SpyChangePct != 0 ? $"{m.SpyChange:+0.00;-0.00} ({m.SpyChangePct:+0.00;-0.00}%)" : "N/A";
            var qqqStr = m.QqqChangePct != 0 ? $"{m.QqqChange:+0.00;-0.00} ({m.QqqChangePct:+0.00;-0.00}%)" : "N/A";
            var vixStr = m.Vix != 0 ? $"{m.Vix:F1} ({m.VixLabel} volatility)" : "N/A";

            var user = $"Pre-market snapshot:\nSPY: {spyStr}\nQQQ: {qqqStr}\nVIX: {vixStr}\n" +
                       $"Market mood: {m.MarketLabel}\n\n" +
                       "What does this mean for 2–10 day swing trades opening today?";

            var text = await CallClaudeAsync(claude, system, user, 300, ct);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Market narrative generation failed — using fallback");
            return fallback;
        }
    }

    private async Task<string> GenerateBuyNarrativeAsync(IClaudeClient claude, StockSignal s, CancellationToken ct)
    {
        var volumeLabel = s.VolumeRatio switch { > 2m => "very high", > 1.5m => "elevated", _ => "normal" };
        var macdDir = s.MacdHistogram > 0 ? "building" : "fading";
        var fallback = $"{s.Symbol} showing {s.SetupType} characteristics with conviction score " +
                       $"{s.ConvictionScore:F1}/10. RSI at {s.Rsi14:F1} with {volumeLabel} volume activity.";
        try
        {
            var system = "Write a 2-sentence trading brief for one stock. " +
                         "Sound like a concise analyst, not a robot. Be specific about the technical setup. " +
                         "No bullet points. No headers. No markdown. Return only the 2 sentences, nothing else.";

            var user = $"Symbol: {s.Symbol}\nSetup: {s.SetupType}\nConviction: {s.ConvictionScore:F1}/10\n" +
                       $"RSI: {s.Rsi14:F1}\nMACD histogram: {s.MacdHistogram:F3} ({macdDir} momentum)\n" +
                       $"Volume: {s.VolumeRatio:F1}x average\n" +
                       $"Sentiment: {s.SentimentScore:F2} — {s.NewsSummary ?? "no recent news"}\n\n" +
                       "Write a brief for why this is worth watching for a swing entry today.";

            var text = await CallClaudeAsync(claude, system, user, 300, ct);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Buy narrative failed for {Symbol} — using fallback", s.Symbol);
            return fallback;
        }
    }

    // Deliberately not Claude-generated: an LLM occasionally worded this
    // contradictorily (e.g. calling the same negative figure a "loss" in one
    // clause and a "gain" in the next). This single P&L number can only ever
    // be a gain, a loss, or flat — a plain conditional template can never get
    // that wrong the way free-form generation can, and the sentence is basic
    // enough that it doesn't need an LLM at all.
    private static Task<string> GeneratePortfolioNarrativeAsync(PortfolioState p, decimal gbpUsd)
    {
        // Unrealised P&L is derived from USD prices — convert to GBP. Cash is
        // already GBP (T212 account summary), so leave it as-is.
        var totalUnrealisedPnl = p.Positions.Sum(x => x.UnrealisedAmt) * gbpUsd;
        var totalUnrealisedPct = p.Positions.Count > 0
            ? p.Positions.Average(x => x.UnrealisedPct) : 0m;

        string positionsSentence;
        if (p.Positions.Count == 0)
        {
            positionsSentence = "You have no open positions.";
        }
        else
        {
            var plural = p.Positions.Count == 1 ? "position" : "positions";
            var direction = totalUnrealisedPnl switch { > 0 => "gain", < 0 => "loss", _ => "" };
            var pnlClause = direction == ""
                ? "no unrealised profit or loss"
                : $"an unrealised {direction} of £{Math.Abs(totalUnrealisedPnl):F2}, " +
                  $"representing a {Math.Abs(totalUnrealisedPct):F2}% {direction}";
            positionsSentence = $"You have {p.Positions.Count} open {plural} with {pnlClause} on your portfolio.";
        }

        string tradesSentence = p.Stats.ClosedTradeCount == 0
            ? "There are no closed trades in the last 30 days"
            : $"There {(p.Stats.ClosedTradeCount == 1 ? "is" : "are")} {p.Stats.ClosedTradeCount} " +
              $"closed {(p.Stats.ClosedTradeCount == 1 ? "trade" : "trades")} in the last 30 days " +
              $"with a {p.Stats.WinRate:P0} win rate";

        return Task.FromResult($"{positionsSentence} {tradesSentence}, with £{p.CashAvailable:F2} in available cash.");
    }

    private async Task<string> CallClaudeAsync(IClaudeClient claude, string system, string user, int maxTokens, CancellationToken ct)
    {
        var cfg = claudeConfig.Value;
        var request = new ClaudeRequest(cfg.Model, maxTokens, system, [new ClaudeMessage("user", user)]);
        await claudeRateLimiter.WaitAsync(ct);
        var response = await claude.SendMessageAsync(request);
        var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
        return StripCodeFences(raw).Trim();
    }

    // ── Step 5 ───────────────────────────────────────────────────────────────

    private async Task CalculateEntryLevelsAsync(int accountId, List<StockSignal> buys, IFinnhubClient finnhub, CancellationToken ct)
    {
        // Stop/target now come from the risk profile (plain settings, not the
        // old per-setup/per-conviction tables) - same values Execution uses.
        var profile = await riskProfileRepo.GetAsync(accountId, ct);

        foreach (var signal in buys)
        {
            var price = signal.CurrentPrice;
            try
            {
                var q = await finnhub.GetQuoteAsync(signal.Symbol);
                if (q.CurrentPrice > 0) price = q.CurrentPrice!.Value;
            }
            catch { /* use signal.CurrentPrice */ }

            var (stopLoss, target) = EntryLevelCalculator.Calculate(price, profile.StopLossPct, profile.TargetPct);

            var gain = target - price;
            var risk = price - stopLoss;
            var rrRatio = risk > 0 ? Math.Round(gain / risk, 2) : 0m;

            signal.CalculatedStopLoss = stopLoss;
            signal.CalculatedTarget = target;
            signal.RiskRewardRatio = rrRatio;

            await signalRepo.UpdateAsync(signal);
        }
    }

    // ── Step 6 ───────────────────────────────────────────────────────────────

    private string BuildMarkdown(
        DateOnly reportDate, List<StockSignal> buys, List<StockSignal> watches,
        int holds, List<StockSignal> avoids, PortfolioState portfolio,
        MarketContext market, ReportNarratives narratives, ReportConfig cfg,
        decimal gbpUsd, FunnelShadowStats? funnel = null)
    {
        var sb = new StringBuilder();
        var now = ToEastern(DateTime.UtcNow);
        var runTime = $"{cfg.RunHourEastern}:{cfg.RunMinuteEastern:D2}";

        sb.AppendLine($"# \U0001F4CA Acme Trading Daily Brief");
        sb.AppendLine($"### {reportDate.DayOfWeek} {reportDate:dd MMM yyyy}");
        sb.AppendLine($"#### {narratives.MarketContext}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Buys ──────────────────────────────────────────────────────────
        sb.AppendLine($"## \U0001F7E2 Top Buys — {buys.Count} Signal(s)");
        sb.AppendLine();

        if (buys.Count == 0)
        {
            var topWatch = watches.FirstOrDefault();
            sb.AppendLine(topWatch is not null
                ? $"> No BUY signals today. Highest watch: {topWatch.Symbol} at {topWatch.ConvictionScore:F1}/10."
                : "> No BUY signals today.");
        }
        else
        {
            foreach (var (buy, rank) in buys.Take(cfg.MaxBuysInReport).Select((b, i) => (b, i + 1)))
            {
                var narrative = narratives.BuyNarratives.GetValueOrDefault(buy.Symbol, string.Empty);
                var stopPct = buy.CalculatedStopLoss.HasValue
                    ? (buy.CurrentPrice - buy.CalculatedStopLoss.Value) / buy.CurrentPrice * 100m : 5m;
                var targetPct = buy.CalculatedTarget.HasValue
                    ? (buy.CalculatedTarget.Value - buy.CurrentPrice) / buy.CurrentPrice * 100m : 8m;
                // Prices are USD — convert to GBP for display (% risk/gain are
                // currency-invariant ratios and stay computed on the raw values).
                var entryLow = Math.Round(buy.CurrentPrice * 0.995m * gbpUsd, 2);
                var entryHigh = Math.Round(buy.CurrentPrice * 1.005m * gbpUsd, 2);
                var stopLossGbp = buy.CalculatedStopLoss * gbpUsd;
                var targetGbp = buy.CalculatedTarget * gbpUsd;

                sb.AppendLine($"### {rank}. {buy.Symbol}");
                sb.AppendLine($"**Conviction {buy.ConvictionScore:F1}/10 · {buy.SetupType} · Sentiment {buy.SentimentScore:+0.00;-0.00;0.00}**");
                sb.AppendLine();
                sb.AppendLine(narrative);
                sb.AppendLine();
                sb.AppendLine("| | |");
                sb.AppendLine("|---|---|");
                sb.AppendLine($"| Entry zone | £{entryLow:F2} – £{entryHigh:F2} |");
                sb.AppendLine($"| Stop loss | £{stopLossGbp:F2} ({stopPct:F1}% risk) |");
                sb.AppendLine($"| Target | £{targetGbp:F2} ({targetPct:F1}% gain) |");
                sb.AppendLine($"| Risk/Reward | {buy.RiskRewardRatio:F1}:1 |");
                sb.AppendLine("| Est. hold | 3–8 days |");
                if (buy.SectorEtf is not null && buy.RelativeReturn.HasValue)
                    sb.AppendLine($"| Sector ({buy.SectorEtf}) | {buy.RelativeReturn:+0.0;-0.0}% 5d vs sector |");
                var supportLabel = buy.NearestSupport.HasValue ? $"£{buy.NearestSupport.Value * gbpUsd:F2}" : "None in range";
                var resistLabel = buy.NearestResistance.HasValue ? $"£{buy.NearestResistance.Value * gbpUsd:F2}" : "Clear above";
                sb.AppendLine($"| Support | {supportLabel} |");
                sb.AppendLine($"| Resistance | {resistLabel} |");
                sb.AppendLine();

                if (buy.RiskRewardRatio < 1.5m)
                    sb.AppendLine("> ⚠️ R:R below 1.5 — consider smaller size");

                if (buy.EarningsSetupType == EarningsSetupType.PostEarningsBeat)
                    sb.AppendLine($"> \U0001F4E3 Beat earnings by {buy.EpsSurprisePct:+0.0}% — momentum may continue");
                else if (buy.EarningsSetupType == EarningsSetupType.PostEarningsNeutral && buy.DaysSinceEarnings.HasValue)
                    sb.AppendLine($"> \U0001F4E3 Reported earnings {buy.DaysSinceEarnings}d ago — in line with estimates");

                if (buy.PriceLevelContext != PriceLevelContext.BetweenLevels &&
                    buy.PriceLevelContext != PriceLevelContext.InsufficientData &&
                    buy.Reasoning is not null)
                {
                    var plLabel = buy.Reasoning.Split('.').LastOrDefault(s => s.TrimStart().StartsWith("Trading near")
                        || s.TrimStart().StartsWith("Just broke")
                        || s.TrimStart().StartsWith("Approaching")
                        || s.TrimStart().StartsWith("Above all"));
                    if (!string.IsNullOrWhiteSpace(plLabel))
                        sb.AppendLine($"> \U0001F4CD {plLabel.Trim()}");
                }

                if (buy.FundamentalNarrative is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Fundamentals:** {buy.FundamentalNarrative}");

                    if (buy.AnalystTrend is Core.Enums.AnalystTrend.StronglyBullish or Core.Enums.AnalystTrend.Bullish)
                        sb.AppendLine($"> \U0001F3E6 Analysts bullish (consensus improving)");

                    if (buy.InsiderActivity == Core.Enums.InsiderActivity.StrongBuying)
                        sb.AppendLine("> \U0001F454 Insiders purchased in last 90 days");
                    else if (buy.InsiderActivity == Core.Enums.InsiderActivity.ClusterSelling)
                        sb.AppendLine("> ⚠️ Insiders sold recently — consider smaller position size");
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();

        // ── Watches ───────────────────────────────────────────────────────
        sb.AppendLine($"## \U0001F440 Watching ({watches.Count})");
        sb.AppendLine();
        if (watches.Count == 0)
        {
            sb.AppendLine("> No Watch signals today.");
        }
        else
        {
            foreach (var w in watches)
            {
                var earningsNote = w.DaysUntilEarnings.HasValue ? $" ⚠️ Earnings in {w.DaysUntilEarnings}d" : string.Empty;
                sb.AppendLine($"**{w.Symbol}**{earningsNote} {w.ConvictionScore:F1}/10 — {w.NewsSummary ?? w.SetupType.ToString()}");

                if (w.AnalystTrend.HasValue && w.AnalystTrend != Core.Enums.AnalystTrend.Insufficient
                    && w.EarningsConsistency.HasValue && w.EarningsConsistency != Core.Enums.EarningsConsistency.Insufficient)
                {
                    sb.AppendLine($"{w.Symbol}: {w.AnalystTrend} analyst consensus, {w.EarningsConsistency} earnings track record");
                }
            }
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Open Positions ────────────────────────────────────────────────
        sb.AppendLine($"## \U0001F4C2 Open Positions ({portfolio.Positions.Count})");
        sb.AppendLine();
        if (portfolio.Positions.Count == 0)
        {
            sb.AppendLine("> No open positions. Full cash available.");
        }
        else
        {
            foreach (var pos in portfolio.Positions)
            {
                var holdingValueGbp = pos.CurrentPrice * pos.Trade.Quantity * gbpUsd;
                var unrealisedGbp = pos.UnrealisedAmt * gbpUsd;
                sb.AppendLine(
                    $"**{pos.Trade.Symbol}** · Holding £{holdingValueGbp:F2} ({pos.Trade.Quantity:0.####} sh) · {pos.DaysHeld}d · " +
                    $"{unrealisedGbp:+£0.00;-£0.00;£0.00} ({pos.UnrealisedPct:+0.00;-0.00;0.00}%)");
                if (pos.IsNearStop)
                    sb.AppendLine($"> \U0001F534 Within {pos.PctFromStop:F1}% of stop — monitor closely");
                if (pos.IsNearTarget)
                    sb.AppendLine($"> \U0001F7E2 Within {pos.PctFromTarget:F1}% of target — consider exit");
                if (pos.IsTimeAlert)
                    sb.AppendLine($"> ⏰ {pos.DaysHeld} days held — thesis may not be playing out");
                sb.AppendLine();
            }
        }
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Performance ───────────────────────────────────────────────────
        sb.AppendLine("## \U0001F4C8 Performance");
        sb.AppendLine();
        sb.AppendLine(narratives.PortfolioNarrative);
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Cash available | £{portfolio.CashAvailable:F2} |");
        sb.AppendLine($"| Open positions | {portfolio.Positions.Count} |");
        sb.AppendLine($"| 30d win rate | {portfolio.Stats.WinRate:P0} ({portfolio.Stats.ClosedTradeCount} trades) |");
        sb.AppendLine($"| 30d exp. value | {portfolio.Stats.Ev:+0.00;-0.00}% per trade |");
        sb.AppendLine($"| Capital tier | {portfolio.Snapshot?.CurrentTier.ToString() ?? "Tier1"} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Avoids ────────────────────────────────────────────────────────
        sb.AppendLine("## \U0001F6AB Avoiding Today");
        sb.AppendLine();
        if (avoids.Count == 0)
        {
            sb.AppendLine("None flagged.");
        }
        else
        {
            var parts = avoids.Select(a =>
            {
                var reason = a.Rsi14 > 75 ? "overbought"
                    : a.SentimentScore < -0.3m ? "negative news"
                    : a.ConvictionScore < 2m ? "no setup"
                    : "low conviction";
                return $"{a.Symbol} ({reason})";
            });
            sb.AppendLine(string.Join(", ", parts));
        }

        // Funnel shadow (Phase F1): the divergence evidence the F2 flip is
        // gated on, one line per day. "+" = gate-only would-Buy, "-" =
        // legacy-only Buy.
        if (funnel is { Scored: > 0 })
        {
            sb.AppendLine();
            var divergent = funnel.DivergentSymbols.Count == 0
                ? "none"
                : string.Join(", ", funnel.DivergentSymbols.Take(8)) +
                  (funnel.DivergentSymbols.Count > 8 ? $" +{funnel.DivergentSymbols.Count - 8} more" : "");
            sb.AppendLine(
                $"*Funnel shadow: {funnel.Scored} scored · legacy Buy {funnel.LegacyBuys} · gate would-Buy {funnel.GateWouldBuy} · " +
                $"divergent: {divergent} · would-veto {funnel.WouldVeto}.*");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        var totalSignals = buys.Count + watches.Count + holds + avoids.Count;
        sb.AppendLine($"*{now:HH:mm} ET · Research: {totalSignals} signals · Next run: tomorrow {runTime} ET*");

        return sb.ToString();
    }

    // Kept separate from BuildMarkdown so this reminder can be emailed only
    // to recipients with TradeApproval ticked, rather than to everyone who
    // gets the general daily report. Just a reminder - approving actually
    // happens in the app's Trades > Approvals tab, not via a link in the
    // email, so there's no token/symbols to construct here.
    private string BuildApprovalMarkdown(DateOnly reportDate)
    {
        var baseUrl = approvalConfig.Value.BaseUrl.TrimEnd('/');
        var closeH = approvalConfig.Value.ApprovalWindowCloseHourEt;
        var closeM = approvalConfig.Value.ApprovalWindowCloseMinuteEt;

        var sb = new StringBuilder();
        sb.AppendLine($"# \U0001F6A6 Action Required: Approve Today's Trades — {reportDate:dd MMM yyyy}");
        sb.AppendLine();
        sb.AppendLine("Today's trades are ready for review.");
        sb.AppendLine();
        sb.AppendLine($"**Go to {baseUrl}/trades?tab=approvals to approve or reject them.**");
        sb.AppendLine();
        sb.AppendLine($"_Approval window closes {closeH}:{closeM:D2} AM ET. No action = no trades today._");

        return sb.ToString();
    }

    // ── Step 7 ───────────────────────────────────────────────────────────────

    private async Task<DailyReport> PersistAsync(
        int accountId, TradingMode tradingMode, DateOnly reportDate, string markdown, string? approvalMarkdown, List<StockSignal> buys,
        PortfolioState portfolio, string marketContext, bool approvalRequired)
    {
        var topBuys = buys.Take(reportConfig.Value.MaxBuysInReport).ToList();
        var topBuySymbols = topBuys.Select(b => b.Symbol).ToList();
        var candidatesJson = JsonSerializer.Serialize(topBuys.Select(b => new
        {
            b.Symbol,
            b.CompanyName,
            SetupType = b.SetupType.ToString(),
            Conviction = b.ConvictionScore,
            Price = b.CurrentPrice,
            Stop = b.CalculatedStopLoss,
            Target = b.CalculatedTarget,
            RiskReward = b.RiskRewardRatio,
        }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var nearStopSymbols = portfolio.Positions
                                       .Where(p => p.IsNearStop)
                                       .Select(p => p.Trade.Symbol).ToList();
        var totalUnrealisedPnl = portfolio.Positions.Sum(p => p.UnrealisedAmt);

        // Regenerating a report for a date that already has one (e.g. the manual
        // "Run Report" button clicked twice in a day) must overwrite it rather
        // than insert a duplicate — (AccountId, ReportDate) is effectively unique.
        var report = await reportRepo.GetByDateAsync(accountId, tradingMode, reportDate);
        if (report is null)
        {
            report = new DailyReport { AccountId = accountId, TradingMode = tradingMode, ReportDate = reportDate };
        }

        report.ReportMarkdown = markdown;
        report.ApprovalMarkdown = approvalMarkdown;
        report.TopBuysJson = JsonSerializer.Serialize(topBuySymbols);
        report.TopSellsJson = JsonSerializer.Serialize(nearStopSymbols);
        report.MarketContext = marketContext;
        report.PortfolioValue = portfolio.Snapshot?.TotalCapital ?? 0m;
        report.DailyPnl = totalUnrealisedPnl;
        report.WasSent = false;

        if (report.Id == 0)
            await reportRepo.AddAsync(report);
        else
            await reportRepo.UpdateAsync(report);

        if (approvalRequired)
        {
            // Regenerating a report for a date that already has a TradeApproval
            // (e.g. the manual "Run Report" button clicked twice) must not
            // insert a duplicate row - GetByDateAsync/the Approvals list both
            // assume one row per (AccountId, TradeDate). If one already
            // exists, leave its IsApproved/ApprovedAt alone rather than
            // silently un-approving something the user already confirmed.
            var existingApproval = await approvalRepo.GetByDateAsync(accountId, tradingMode, reportDate);
            if (existingApproval is null)
            {
                var approval = new TradeApproval
                {
                    AccountId = accountId,
                    TradingMode = tradingMode,
                    TradeDate = reportDate,
                    IsApproved = false,
                    IsExpired = false,
                    CandidatesJson = candidatesJson,
                };
                await approvalRepo.AddAsync(approval);
                logger.LogInformation("Approval row created for {Date} (account {AccountId})", reportDate, accountId);
            }
        }

        return report;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline >= 0) t = t[(firstNewline + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
        }
        return t.Trim();
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static DateTime ToEastern(DateTime utc)
    {
        TimeZoneInfo eastern;
        try { eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        catch { eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc), eastern);
    }
}

// ── Internal state records ────────────────────────────────────────────────────

internal record OpenPositionState(
    Trade Trade,
    decimal CurrentPrice,
    decimal UnrealisedAmt,
    decimal UnrealisedPct,
    int DaysHeld,
    decimal PctFromStop,
    decimal PctFromTarget,
    bool IsNearStop,
    bool IsNearTarget,
    bool IsTimeAlert);

internal record PortfolioState(
    List<OpenPositionState> Positions,
    PortfolioSnapshot? Snapshot,
    decimal CashAvailable,
    PerformanceStats Stats);

internal record PerformanceStats(
    decimal WinRate,
    decimal AvgWinPct,
    decimal AvgLossPct,
    decimal Ev,
    int ClosedTradeCount,
    string? Note);

internal record MarketContext(
    decimal SpyPrice,
    decimal SpyChange,
    decimal SpyChangePct,
    decimal QqqPrice,
    decimal QqqChange,
    decimal QqqChangePct,
    decimal Vix,
    string VixLabel,
    string MarketLabel,
    string TechLabel);

internal record ReportNarratives(
    string MarketContext,
    Dictionary<string, string> BuyNarratives,
    string PortfolioNarrative);
