using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Fundamental;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Research;

// Stateless by design (unlike the legacy single-tenant version, which cached
// weights/regime/earnings-context in instance fields): a consumer processes
// several symbols for one account concurrently (see the Phase 10d consumer
// pattern's SemaphoreSlim batch), and instance fields reset at the top of
// RunAsync would corrupt state across those concurrent calls sharing one
// injected instance. Everything call-scoped is threaded as a parameter
// instead of stored on `this`.
public class ResearchPipeline(
    ICandleRepository candleRepo,
    ISignalRepository signalRepo,
    ITradeRepository tradeRepo,
    IIndicatorService indicators,
    IStrategyWeightsRepository weightsRepo,
    IEarningsService earningsService,
    IRelativeStrengthService relativeStrengthService,
    IPriceLevelService priceLevelService,
    IMarketRegimeService marketRegimeService,
    IFundamentalDataService fundamentalDataService,
    IFundamentalScoringService fundamentalScoringService,
    ITiingoRateLimiter tiingoRateLimiter,
    IFinnhubRateLimiter finnhubRateLimiter,
    IOptions<ClaudeConfig> claudeConfig,
    IOptions<ResearchConfig> researchConfig,
    IOptions<EarningsConfig> earningsConfig,
    ILogger<ResearchPipeline> logger) : IResearchPipeline
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly record struct ComponentScores(
        decimal Rsi, decimal Macd, decimal Volume, decimal Sentiment, decimal Setup);

    public async Task<StockSignal?> RunAsync(
        int accountId, IFinnhubClient finnhub, ITiingoClient tiingo, IClaudeClient claude,
        string symbol, AccountRiskProfile riskProfile,
        IReadOnlyDictionary<string, IReadOnlyList<StockCandle>>? freshCandlesBySymbol = null,
        CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();

        // Step 0 — earnings gate: block BUY if earnings are within GateDays
        var earningsCtx = await earningsService.GetEarningsContextAsync(finnhub, symbol, ct, riskProfile.EarningsGateDays);

        if (earningsCtx.SetupType == EarningsSetupType.UpcomingEarnings)
        {
            logger.LogInformation("{Symbol}: earnings in {Days} days — returning Hold", symbol, earningsCtx.DaysUntilEarnings);
            var gateSignal = new StockSignal
            {
                AccountId = accountId,
                Symbol = symbol,
                SignalDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CurrentPrice = 0m,
                Recommendation = Recommendation.Hold,
                ConvictionScore = 0.0m,
                EarningsSetupType = EarningsSetupType.UpcomingEarnings,
                DaysUntilEarnings = earningsCtx.DaysUntilEarnings,
                Reasoning = $"Earnings report in {earningsCtx.DaysUntilEarnings} days — binary event, avoiding. Will reassess post-announcement.",
                WasExecuted = false,
            };
            await signalRepo.UpsertAsync(gateSignal);
            return gateSignal;
        }

        var candles = await FetchAndStoreCandlesAsync(accountId, tiingo, symbol, freshCandlesBySymbol, ct);
        if (candles is null || candles.Count == 0)
            return null;

        var (weights, regime) = await GetWeightsAndRegimeAsync(accountId, tiingo, finnhub, symbol, ct);
        var ind = await CalculateIndicatorsAsync(candles);
        var (sentimentScore, newsSummary) = await FetchAndScoreSentimentAsync(finnhub, claude, symbol, ct);
        var setupType = DetectSetup(ind, candles);

        // MACD's "rising" distinction needs yesterday's histogram — recompute MACD on the
        // candle window with today's bar dropped rather than depending on a stored prior
        // StockSignal row (works even for symbols scored for the first time).
        decimal? previousMacdHistogram = null;
        if (candles.Count > 1)
        {
            var (_, _, prevHistogram) = await indicators.GetMacdAsync(candles.Take(candles.Count - 1));
            previousMacdHistogram = prevHistogram;
        }

        var componentScores = ScoreComponents(ind, sentimentScore, setupType, previousMacdHistogram);
        RelativeStrengthResult? rs = null;
        try
        {
            rs = await relativeStrengthService.CalculateAsync(tiingo, symbol, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Relative strength service failed for {Symbol} — skipping RS score", symbol);
        }

        PriceLevelResult? priceLevel = null;
        try
        {
            priceLevel = await priceLevelService.AnalyseAsync(symbol, candles[^1].Close, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Price level service failed for {Symbol} — skipping price level score", symbol);
        }

        FundamentalSnapshot? fundamentalSnapshot = null;
        FundamentalScore? fundamental = null;
        try
        {
            var earningsHistory = earningsCtx.EarningsHistory ?? [];
            fundamentalSnapshot = await fundamentalDataService.GetSnapshotAsync(finnhub, symbol, earningsHistory, ct);
            fundamental = await fundamentalScoringService.ScoreAsync(claude, symbol, fundamentalSnapshot, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fundamental scoring failed for {Symbol} — using neutral default", symbol);
        }

        var conviction = ConvictionScorer.Calculate(
            weights,
            componentScores.Rsi,
            componentScores.Macd,
            componentScores.Volume,
            componentScores.Sentiment,
            componentScores.Setup,
            relativeStrengthScore: rs?.Score ?? 0.5m,
            priceLevelScore: priceLevel?.Score ?? 0.5m,
            fundamentalMomentumScore: fundamental?.Score ?? 0.5m);

        conviction = ApplyEarningsAdjustment(conviction, earningsCtx, out var earningsReasoning);

        var recommendation = await DetermineRecommendationAsync(accountId, symbol, ind, conviction, weights);

        return await PersistSignalAsync(accountId, symbol, candles[^1], ind, sentimentScore,
            newsSummary, setupType, conviction, recommendation, componentScores, regime, earningsCtx, rs, priceLevel,
            earningsReasoning, fundamentalSnapshot, fundamental);
    }

    private async Task<(StrategyWeights Weights, MarketRegime? Regime)> GetWeightsAndRegimeAsync(
        int accountId, ITiingoClient tiingo, IFinnhubClient finnhub, string symbol, CancellationToken ct)
    {
        MarketRegime? regime = null;
        try
        {
            var regimeResult = await marketRegimeService.GetCurrentRegimeAsync(tiingo, finnhub, ct);
            regime = regimeResult.Regime;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Market regime detection failed — falling back to general weights");
        }

        var weights = await weightsRepo.GetActiveWeightsAsync(accountId, regime)
            ?? new StrategyWeights(); // fallback to defaults if DB empty

        logger.LogDebug(
            "Using {Regime} weights for {Symbol}",
            weights.ApplicableRegime?.ToString() ?? "general",
            symbol);

        return (weights, regime);
    }

    private async Task<List<StockCandle>?> FetchAndStoreCandlesAsync(
        int accountId, ITiingoClient tiingo, string symbol,
        IReadOnlyDictionary<string, IReadOnlyList<StockCandle>>? freshCandlesBySymbol, CancellationToken ct)
    {
        var cfg = researchConfig.Value;
        var from = DateTime.UtcNow.AddDays(-cfg.CandleHistoryDays);
        var to = DateTime.UtcNow;

        // Skip the Tiingo round-trip entirely if we already fetched today's
        // candle for this symbol - a big win when Research is re-run more than
        // once in the same day (manual triggers, retries), which previously
        // re-pulled full history for every symbol every time and was a major
        // contributor to burning through Tiingo's rate/quota limit.
        //
        // The freshness map is looked up ONCE per job (by ResearchConsumerFunction,
        // before the concurrent per-symbol loop starts) rather than queried here -
        // a per-symbol DbContext read at this point, with up to MaxConcurrentSymbols
        // callers and no delay in front of it, hit EF Core's single-operation-at-a-
        // time guard almost immediately (the same class of bug the risk profile
        // fix addressed).
        if (freshCandlesBySymbol is not null && freshCandlesBySymbol.TryGetValue(symbol, out var stored) && stored.Count > 0)
        {
            logger.LogInformation("{Symbol}: already have today's candle — reusing stored history, skipping Tiingo", symbol);
            return stored.OrderBy(c => c.Timestamp).ToList();
        }

        logger.LogInformation("Fetching candles for {Symbol} from {From:yyyy-MM-dd} to {To:yyyy-MM-dd}",
            symbol, from, to);

        List<TiingoDailyPrice> prices;
        try
        {
            await tiingoRateLimiter.WaitAsync(ct);
            prices = await tiingo.GetDailyPricesAsync(
                symbol,
                from.ToString("yyyy-MM-dd"),
                to.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tiingo candle fetch failed for {Symbol}", symbol);
            return null;
        }

        if (prices.Count == 0)
        {
            logger.LogWarning("Tiingo returned no data for {Symbol} — skipping", symbol);
            return null;
        }

        var candles = prices.Select(p => new StockCandle
        {
            Symbol = symbol,
            Timestamp = p.Date.ToUniversalTime(),
            Open = p.AdjOpen,
            High = p.AdjHigh,
            Low = p.AdjLow,
            Close = p.AdjClose,
            Volume = (long)p.AdjVolume,
            Resolution = "D",
            CreatedAt = DateTime.UtcNow,
        }).ToList();

        await candleRepo.SaveCandlesAsync(accountId, candles);
        return candles;
    }

    private async Task<IndicatorResult> CalculateIndicatorsAsync(List<StockCandle> candles)
    {
        var result = await indicators.CalculateAllAsync(candles);

        if (result.Rsi14 is null) logger.LogDebug("RSI14 null — insufficient candles");
        if (result.Macd is null) logger.LogDebug("MACD null — insufficient candles");
        if (result.Ema21 is null) logger.LogDebug("EMA21 null — insufficient candles");

        return result;
    }

    private async Task<(decimal score, string summary)> FetchAndScoreSentimentAsync(
        IFinnhubClient finnhub, IClaudeClient claude, string symbol, CancellationToken ct)
    {
        var cfg = researchConfig.Value;
        var from = DateTime.UtcNow.AddDays(-cfg.NewsLookbackDays);
        var to = DateTime.UtcNow;

        List<FinnhubNewsItem> news;
        try
        {
            await finnhubRateLimiter.WaitAsync(ct);
            news = await finnhub.GetCompanyNewsAsync(
                symbol, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch news for {Symbol}", symbol);
            return (0.0m, "News fetch failed.");
        }

        if (news.Count == 0)
            return (0.0m, "No recent news found.");

        var articles = news.OrderByDescending(n => n.Datetime)
                           .Take(cfg.MaxNewsArticles)
                           .ToList();

        var articlesText = string.Join("\n---\n", articles.Select(n =>
            $"Headline: {n.Headline}\nSummary: {n.Summary}"));

        var systemPrompt =
            "You are a financial sentiment analyst. " +
            "Respond only with valid JSON. No preamble, no markdown, no explanation outside the JSON.";

        var userPrompt =
            $"Analyse the sentiment of these news headlines and summaries for stock ticker {symbol}.\n\n" +
            $"Articles:\n{articlesText}\n\n" +
            "Respond with this exact JSON structure:\n" +
            "{\n" +
            "  \"sentiment_score\": <float between -1.0 and 1.0>,\n" +
            "  \"summary\": \"<2-3 sentence summary of key themes>\",\n" +
            "  \"key_factors\": [\"<factor 1>\", \"<factor 2>\"]\n" +
            "}\n\n" +
            "sentiment_score guide:\n" +
            "-1.0 = extremely negative (fraud, bankruptcy, disaster)\n" +
            "-0.5 = moderately negative (missed earnings, downgrade)\n" +
            " 0.0 = neutral or mixed\n" +
            "+0.5 = moderately positive (beat earnings, upgrade)\n" +
            "+1.0 = extremely positive (major acquisition, breakthrough)";

        try
        {
            var request = new ClaudeRequest(
                claudeConfig.Value.Model,
                claudeConfig.Value.MaxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            var response = await claude.SendMessageAsync(request);
            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
            var text = StripCodeFences(raw);

            var parsed = JsonSerializer.Deserialize<ClaudeSentimentResult>(text, JsonOpts);
            if (parsed is null)
                throw new JsonException("null result");

            return ((decimal)parsed.SentimentScore, parsed.Summary);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude sentiment analysis failed for {Symbol} — defaulting to 0.0", symbol);
            return (0.0m, "Sentiment analysis unavailable.");
        }
    }

    private static SetupType DetectSetup(IndicatorResult ind, List<StockCandle> candles)
    {
        var price = candles[^1].Close;

        if (ind.Rsi14 < 35 && ind.BollingerLower.HasValue && price > ind.BollingerLower.Value)
        {
            if (candles.Count >= 4)
            {
                var recentPrices = candles.TakeLast(4).Select(c => c.Close).ToList();
                if (recentPrices[^1] > recentPrices[0])
                    return SetupType.OversoldRecovery;
            }
            return SetupType.OversoldRecovery;
        }

        if (ind.BollingerUpper.HasValue && price > ind.BollingerUpper.Value
            && ind.VolumeRatio > 1.5m && ind.MacdHistogram > 0)
            return SetupType.Breakout;

        if (ind.Rsi14 >= 50 && ind.Rsi14 <= 65
            && ind.Ema9.HasValue && ind.Ema21.HasValue && ind.Ema9 > ind.Ema21
            && ind.MacdHistogram > 0 && ind.VolumeRatio > 1.0m)
            return SetupType.MomentumContinuation;

        if (ind.VolumeRatio > 2.0m && candles.Count >= 2)
        {
            var prev = candles[^2].Close;
            var change = (price - prev) / prev * 100;
            if (change > 1.5m)
                return SetupType.VolumeSpike;
        }

        if (ind.Ema9.HasValue && ind.Ema21.HasValue && ind.Ema9 > ind.Ema21
            && ind.Rsi14 > 50
            && ind.BollingerMid.HasValue && price > ind.BollingerMid.Value)
            return SetupType.TrendFollowing;

        return SetupType.Unknown;
    }

    // Thin orchestration: fetch component values, score them via ConvictionScorer, return.
    // No scoring logic lives here — see ConvictionScorer.cs.
    private static ComponentScores ScoreComponents(
        IndicatorResult ind, decimal sentimentScore, SetupType setup, decimal? previousHistogram) =>
        new(
            Rsi: ConvictionScorer.ScoreRsi(ind.Rsi14),
            Macd: ConvictionScorer.ScoreMacd(ind.MacdHistogram, previousHistogram),
            Volume: ConvictionScorer.ScoreVolume(ind.VolumeRatio),
            Sentiment: ConvictionScorer.ScoreSentiment(sentimentScore),
            Setup: ConvictionScorer.ScoreSetupQuality(setup));

    private decimal ApplyEarningsAdjustment(decimal conviction, EarningsContext? ctx, out string? reasoning)
    {
        reasoning = null;
        if (ctx is null || ctx.SetupType == EarningsSetupType.None
            || ctx.SetupType == EarningsSetupType.PostEarningsNeutral)
            return conviction;

        var cfg = earningsConfig.Value;

        if (ctx.SetupType == EarningsSetupType.PostEarningsBeat)
        {
            var boost = Math.Min(Math.Abs(ctx.EpsSurprisePct ?? 0m) / 100m * 2m, cfg.MaxBeatBoost);
            conviction = Math.Min(conviction + boost, 10.0m);
            reasoning = $" Post-earnings beat ({ctx.EpsSurprisePct:+0.0}% EPS surprise) added {boost:F1} pts.";
        }
        else if (ctx.SetupType == EarningsSetupType.PostEarningsMiss)
        {
            var penalty = Math.Min(Math.Abs(ctx.EpsSurprisePct ?? 0m) / 100m * 2m, cfg.MaxMissPenalty);
            conviction = Math.Max(conviction - penalty, 0.0m);
            reasoning = $" Post-earnings miss ({ctx.EpsSurprisePct:+0.0}% EPS surprise) reduced by {penalty:F1} pts.";
        }

        return conviction;
    }

    private async Task<Recommendation> DetermineRecommendationAsync(
        int accountId, string symbol, IndicatorResult ind, decimal conviction, StrategyWeights weights)
    {
        var openTrades = await tradeRepo.GetOpenTradesAsync(accountId);
        if (openTrades.Any(t => t.Symbol == symbol))
            return Recommendation.Hold;

        if (ind.Rsi14 > 75)
            return Recommendation.Avoid;

        if (conviction >= weights.BuyThreshold) return Recommendation.Buy;
        if (conviction >= weights.WatchThreshold) return Recommendation.Watch;
        if (conviction >= 3.0m) return Recommendation.Hold;
        return Recommendation.Avoid;
    }

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

    private async Task<StockSignal> PersistSignalAsync(
        int accountId, string symbol, StockCandle latest, IndicatorResult ind,
        decimal sentimentScore, string newsSummary,
        SetupType setupType, decimal conviction, Recommendation recommendation,
        ComponentScores componentScores, MarketRegime? currentRegime, EarningsContext? earningsCtx,
        RelativeStrengthResult? rs = null,
        PriceLevelResult? priceLevel = null, string? earningsReasoning = null,
        FundamentalSnapshot? fundamentalSnapshot = null, FundamentalScore? fundamental = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = (await signalRepo.GetByDateAsync(accountId, today))
            .FirstOrDefault(s => s.Symbol == symbol);

        var signal = existing ?? new StockSignal { AccountId = accountId, Symbol = symbol };

        signal.SignalDate = today;
        signal.CurrentPrice = latest.Close;
        signal.Rsi14 = ind.Rsi14;
        signal.Macd = ind.Macd;
        signal.MacdSignal = ind.MacdSignal;
        signal.MacdHistogram = ind.MacdHistogram;
        signal.VolumeRatio = ind.VolumeRatio;
        signal.BollingerUpper = ind.BollingerUpper;
        signal.BollingerLower = ind.BollingerLower;
        signal.BollingerMid = ind.BollingerMid;
        signal.Ema9 = ind.Ema9;
        signal.Ema21 = ind.Ema21;
        signal.SentimentScore = sentimentScore;
        signal.NewsSummary = newsSummary;
        signal.SetupType = setupType;
        signal.ConvictionScore = conviction;
        signal.Recommendation = recommendation;
        // WasExecuted is deliberately left untouched here - signal is either
        // `existing` (already reflects whatever's in the DB - true if
        // ExecutionService already placed a trade off it today) or a brand
        // new StockSignal (defaults to false). Previously this unconditionally
        // reset it to false on every rescoring, so a long-running Research
        // pass reprocessing a symbol later the same day it was already bought
        // would silently make Execution eligible to buy it again - confirmed
        // live (WDAY got bought twice same day this way).
        signal.MarketRegimeAtSignal = currentRegime;

        signal.RsiScore = componentScores.Rsi;
        signal.MacdScore = componentScores.Macd;
        signal.VolumeScore = componentScores.Volume;
        signal.SentimentComponentScore = componentScores.Sentiment;
        signal.SetupQualityScore = componentScores.Setup;

        if (rs is not null)
        {
            signal.RelativeStrengthScore = rs.Score;
            signal.SectorEtf = rs.SectorEtf;
            signal.StockReturn5d = rs.StockReturn5d;
            signal.SectorReturn5d = rs.EtfReturn5d;
            signal.RelativeReturn = rs.RelativeReturn;

            if (rs.Score >= 0.8m || rs.Score <= 0.2m)
            {
                var rsReasoning = $" {rs.Label}.";
                signal.Reasoning = (signal.Reasoning ?? string.Empty) + rsReasoning;
            }
        }
        else
        {
            signal.RelativeStrengthScore = null;
        }

        if (priceLevel is not null)
        {
            signal.PriceLevelScore = priceLevel.Score;
            signal.PriceLevelContext = priceLevel.Context;
            signal.NearestSupport = priceLevel.NearestSupport;
            signal.NearestResistance = priceLevel.NearestResistance;

            if (priceLevel.Context != PriceLevelContext.BetweenLevels &&
                priceLevel.Context != PriceLevelContext.InsufficientData)
            {
                signal.Reasoning = (signal.Reasoning ?? string.Empty) + $" {priceLevel.Label}.";
            }
        }
        else
        {
            signal.PriceLevelScore = null;
        }

        signal.EarningsSetupType = earningsCtx?.SetupType ?? EarningsSetupType.None;
        signal.EpsSurprisePct = earningsCtx?.EpsSurprisePct;
        signal.DaysUntilEarnings = earningsCtx?.DaysUntilEarnings;
        signal.DaysSinceEarnings = earningsCtx?.DaysSinceEarnings;

        if (earningsReasoning is not null)
            signal.Reasoning = (signal.Reasoning ?? string.Empty) + earningsReasoning;

        signal.FundamentalMomentumScore = fundamental?.Score;
        signal.FundamentalNarrative = fundamental?.Reasoning;
        signal.AnalystTrend = fundamentalSnapshot?.AnalystTrend;
        signal.InsiderActivity = fundamentalSnapshot?.InsiderActivity;
        signal.EarningsConsistency = fundamentalSnapshot?.EarningsConsistency;
        signal.RevenueDirection = fundamentalSnapshot?.RevenueDirection;

        if (existing is null)
            return await signalRepo.AddAsync(signal);

        await signalRepo.UpdateAsync(signal);
        return signal;
    }
}
