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
    IAccountRepository accountRepo,
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
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<ClaudeConfig> claudeConfig,
    IOptions<ResearchConfig> researchConfig,
    IOptions<EarningsConfig> earningsConfig,
    ISentimentArchiveRepository sentimentArchive,
    IFilingRepository filingRepo,
    IOptions<FilingDeltaConfig> filingDeltaConfig,
    ILogger<ResearchPipeline> logger) : IResearchPipeline
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly record struct ComponentScores(
        decimal Rsi, decimal Macd, decimal Volume, decimal Sentiment, decimal Setup);

    public async Task<StockSignal?> RunAsync(
        int accountId, IFinnhubClient finnhub, ITiingoClient tiingo, IClaudeClient claude,
        string symbol, AccountRiskProfile riskProfile,
        IReadOnlyDictionary<string, IReadOnlyList<StockCandle>>? freshCandlesBySymbol = null,
        string? companyName = null,
        CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();

        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        // Step 0 — earnings gate: block BUY if earnings are within GateDays
        var earningsCtx = await earningsService.GetEarningsContextAsync(finnhub, symbol, ct, riskProfile.EarningsGateDays);

        if (earningsCtx.SetupType == EarningsSetupType.UpcomingEarnings)
        {
            logger.LogInformation("{Symbol}: earnings in {Days} days — returning Hold", symbol, earningsCtx.DaysUntilEarnings);
            var gateSignal = new StockSignal
            {
                AccountId = accountId,
                Symbol = symbol,
                CompanyName = companyName,
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

        // Stage 1 (the gate) is computed BEFORE any stage-2 fetching: it only
        // needs the technical components, and under FunnelEnabled a sub-Watch
        // gate skips stage 2 entirely (no Claude sentiment call, no
        // fundamentals fetch) - the funnel's cost saving. Earnings adjustment
        // belongs to the gate (docs/funnel-plan).
        var funnelCfg = researchConfig.Value;
        var funnelEnabled = funnelCfg.FunnelEnabled;
        var technicalScores = ScoreComponents(ind, null, setupType, previousMacdHistogram);
        var gateScore = ApplyEarningsAdjustment(
            FunnelScores.Gate(weights, technicalScores.Rsi, technicalScores.Macd, technicalScores.Volume,
                technicalScores.Setup, rs?.Score ?? 0.5m, priceLevel?.Score ?? 0.5m),
            earningsCtx, out var gateEarningsReasoning);
        // Watch threshold from the WEIGHTS row (the same source
        // DetermineRecommendationAsync classifies with), so a symbol that
        // could still classify Watch always gets its forward score.
        var skipStageTwo = funnelEnabled && gateScore < weights.WatchThreshold;

        decimal? sentimentScore = null;
        var newsSummary = "Skipped — gate score below Watch threshold (funnel stage-2 skip).";
        ClaudeCatalystResult? catalyst = null;
        FundamentalSnapshot? fundamentalSnapshot = null;
        FundamentalScore? fundamental = null;

        if (!skipStageTwo)
        {
            (sentimentScore, newsSummary, catalyst) = await FetchAndScoreSentimentAsync(finnhub, tiingo, claude, symbol, ct);
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
        }

        var componentScores = ScoreComponents(ind, sentimentScore, setupType, previousMacdHistogram);

        // Stage 2 (the forward score): catalyst adjustment belongs here -
        // forward-looking information adjusting the forward-looking score.
        // Null when stage 2 was skipped (sub-Watch gate can't trade, so
        // there's nothing to size or veto).
        decimal? forwardScore = null;
        var forwardDegraded = false;
        if (!skipStageTwo)
        {
            var forward = FunnelScores.Forward(
                sentimentScore is null ? null : componentScores.Sentiment,
                fundamental?.Score,
                funnelCfg.ForwardSentimentWeight, funnelCfg.ForwardFundamentalWeight);
            forwardScore = ApplyCatalystAdjustment(forward.Score, catalyst, out _);
            forwardDegraded = forward.Degraded;
        }

        var shadow = new FunnelScores.FunnelShadow(
            gateScore, forwardScore, forwardDegraded,
            WouldPassGate: gateScore >= weights.BuyThreshold,
            WouldBeVetoed: FunnelScores.ShouldVeto(forwardScore, forwardDegraded, riskProfile.ForwardVetoFloor));

        // Which score drives: the flip. Enabled = the gate IS the conviction
        // (ConvictionScore field keeps one meaning downstream - probation,
        // reports, refinement buckets all keep working). Disabled = the
        // legacy 8-component blend exactly as before, catalyst included.
        decimal conviction;
        string? earningsReasoning;
        string? catalystReasoning = null;
        if (funnelEnabled)
        {
            conviction = gateScore;
            earningsReasoning = gateEarningsReasoning;
            // The catalyst note still rides along for auditability - it
            // explains the Forward score shown next to the signal.
            if (forwardScore is not null)
                _ = ApplyCatalystAdjustment(0m, catalyst, out catalystReasoning);
        }
        else
        {
            conviction = ConvictionScorer.Calculate(
                weights,
                componentScores.Rsi,
                componentScores.Macd,
                componentScores.Volume,
                componentScores.Sentiment,
                componentScores.Setup,
                relativeStrengthScore: rs?.Score ?? 0.5m,
                priceLevelScore: priceLevel?.Score ?? 0.5m,
                fundamentalMomentumScore: fundamental?.Score ?? 0.5m);
            conviction = ApplyEarningsAdjustment(conviction, earningsCtx, out earningsReasoning);
            conviction = ApplyCatalystAdjustment(conviction, catalyst, out catalystReasoning);
        }

        var recommendation = await DetermineRecommendationAsync(accountId, account.TradingMode, symbol, ind, conviction, weights, setupType);

        // Funnel Phase F3: the asymmetric veto. A gate-passing Buy whose
        // Forward score sits below the account's floor demotes to Watch -
        // forward information can block a Buy, never create one. Degraded or
        // missing Forward scores never veto (fail-open: a data outage must
        // not stop trading). The signal stays visible as Watch with
        // WouldBeVetoed=true so the scorecard can measure the counterfactual.
        string? vetoReasoning = null;
        if (funnelEnabled && recommendation == Recommendation.Buy && shadow.WouldBeVetoed)
        {
            recommendation = Recommendation.Watch;
            vetoReasoning = $" Forward veto: score {forwardScore:0.0} below floor {riskProfile.ForwardVetoFloor:0.0}.";
            logger.LogInformation("Forward veto for {Symbol}: forward {Forward:0.0} < floor {Floor:0.0}, Buy demoted to Watch",
                symbol, forwardScore, riskProfile.ForwardVetoFloor);
        }

        // All adjustment notes ride the same reasoning-append slot.
        var adjustmentReasoning = (earningsReasoning ?? string.Empty) + (catalystReasoning ?? string.Empty) + (vetoReasoning ?? string.Empty);

        // Filing-delta shadow (docs/filing-delta-plan FD1): the most recent
        // scored filing-language change, decayed to today. Persisted for the
        // scorecard; drives NOTHING until ForwardFilingWeight > 0 (FD2).
        // Strictly best-effort - no filing data is degraded-null, never a
        // failed research run.
        decimal? filingDeltaScore = null;
        string? filingDeltaSummary = null;
        try
        {
            if (await filingRepo.GetLatestNonZeroDeltaAsync(symbol, ct) is { } latestDelta)
            {
                filingDeltaScore = Filings.FilingDeltaMath.EffectiveScore(
                    latestDelta.Delta, latestDelta.FiledAt, DateOnly.FromDateTime(DateTime.UtcNow),
                    filingDeltaConfig.Value.HalfLifeTradingDays);
                filingDeltaSummary = latestDelta.Summary;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Filing-delta lookup failed for {Symbol} — shadow score omitted", symbol);
        }

        return await PersistSignalAsync(accountId, symbol, companyName, candles[^1], ind, sentimentScore,
            newsSummary, setupType, conviction, recommendation, componentScores, regime, earningsCtx, rs, priceLevel,
            adjustmentReasoning.Length > 0 ? adjustmentReasoning : null, fundamentalSnapshot, fundamental, shadow,
            filingDeltaScore, filingDeltaSummary);
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

    // Null score = "couldn't assess" (fetch/Claude failure) - distinct from a
    // genuine 0.0 neutral so the stored component score stays honest for the
    // Refinement agent's correlations. "No news" IS a genuine neutral.
    // Catalyst rides the same Claude call (null = none detected / unparsable).
    private async Task<(decimal? score, string summary, ClaudeCatalystResult? catalyst)> FetchAndScoreSentimentAsync(
        IFinnhubClient finnhub, ITiingoClient tiingo, IClaudeClient claude, string symbol, CancellationToken ct)
    {
        var cfg = researchConfig.Value;
        var from = DateTime.UtcNow.AddDays(-cfg.NewsLookbackDays);
        var to = DateTime.UtcNow;

        // Two sources, each allowed to fail independently - sentiment degrades
        // to whichever feed answered, and only BOTH failing yields the null
        // "unavailable" score (unchanged contract).
        var finnhubOk = true;
        List<NewsArticle> finnhubArticles = [];
        try
        {
            await finnhubRateLimiter.WaitAsync(ct);
            var news = await finnhub.GetCompanyNewsAsync(
                symbol, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
            finnhubArticles = news.Select(n => new NewsArticle(
                "Finnhub", n.Headline, n.Summary,
                DateTimeOffset.FromUnixTimeSeconds(n.Datetime).UtcDateTime, n.Url)).ToList();
        }
        catch (Exception ex)
        {
            finnhubOk = false;
            logger.LogWarning(ex, "Finnhub news fetch failed for {Symbol}", symbol);
        }

        var tiingoOk = cfg.TiingoNewsEnabled;
        List<NewsArticle> tiingoArticles = [];
        if (cfg.TiingoNewsEnabled)
        {
            try
            {
                await tiingoRateLimiter.WaitAsync(ct);
                var news = await tiingo.GetNewsAsync(
                    symbol.ToLowerInvariant(), from.ToString("yyyy-MM-dd"), cfg.MaxNewsArticles * 2);
                tiingoArticles = news.Select(n => new NewsArticle(
                    "Tiingo", n.Title, n.Description, n.PublishedDate, n.Url)).ToList();
            }
            catch (Exception ex)
            {
                tiingoOk = false;
                logger.LogWarning(ex, "Tiingo news fetch failed for {Symbol} — degrading to Finnhub only", symbol);
            }
        }

        if (!finnhubOk && !tiingoOk)
            return (null, "News fetch failed.", null);

        var articles = NewsBlender.Blend(finnhubArticles.Concat(tiingoArticles), cfg.MaxNewsArticles);
        if (articles.Count == 0)
            return (0.0m, "No recent news found.", null);

        var articlesText = string.Join("\n---\n", articles.Select(n =>
            $"[{n.Source}] Headline: {n.Title}\nSummary: {n.Summary}"));

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
            "  \"key_factors\": [\"<factor 1>\", \"<factor 2>\"],\n" +
            "  \"catalyst\": {\n" +
            "    \"detected\": <true only if the articles mention a SPECIFIC, DATED, FORWARD-looking event>,\n" +
            "    \"type\": \"<short label, e.g. 'guidance raise', 'product launch', 'FDA decision', 'contract win', or null>\",\n" +
            $"    \"expected_date\": \"<YYYY-MM-DD if stated or clearly inferable, within the next {cfg.CatalystMaxDaysAhead} days, else null>\",\n" +
            "    \"direction\": \"<bullish or bearish>\",\n" +
            "    \"strength\": <float 0.0-1.0, how market-moving the event is likely to be>\n" +
            "  }\n" +
            "}\n\n" +
            "sentiment_score guide:\n" +
            "-1.0 = extremely negative (fraud, bankruptcy, disaster)\n" +
            "-0.5 = moderately negative (missed earnings, downgrade)\n" +
            " 0.0 = neutral or mixed\n" +
            "+0.5 = moderately positive (beat earnings, upgrade)\n" +
            "+1.0 = extremely positive (major acquisition, breakthrough)\n\n" +
            "catalyst rules: only concrete upcoming events with a date or clear timeframe count - " +
            "vague optimism, analyst opinions, and PAST events are not catalysts. " +
            "Earnings report dates are NOT catalysts (they are handled separately). " +
            "If nothing qualifies, set detected to false and the other catalyst fields to null/0.";

        try
        {
            var request = new ClaudeRequest(
                claudeConfig.Value.Model,
                claudeConfig.Value.MaxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            await claudeRateLimiter.WaitAsync(ct);
            var response = await claude.SendMessageAsync(request);
            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
            var text = StripCodeFences(raw);

            var parsed = JsonSerializer.Deserialize<ClaudeSentimentResult>(text, JsonOpts);
            if (parsed is null)
                throw new JsonException("null result");

            var score = (decimal)parsed.SentimentScore;
            await ArchiveSentimentAsync(symbol, score, articles, ct);

            // Momentum tilt: the RAW level is what gets archived above (so
            // the archive stays a clean record of what the news actually
            // said each day); the blended value is what feeds conviction.
            var momentum = await BlendSentimentMomentumAsync(symbol, score, ct);

            // Per-source counts in the stored summary so the blend is
            // auditable from the signal itself.
            var finnhubCount = articles.Count(a => a.Source == "Finnhub");
            var tiingoCount = articles.Count(a => a.Source == "Tiingo");
            var momentumNote = momentum.Delta is { } d
                ? $" Sentiment momentum {d:+0.00;-0.00} vs {momentum.HistoryCount}-day archive average."
                : string.Empty;
            return (momentum.BlendedScore,
                $"{parsed.Summary} (Sources: {finnhubCount} Finnhub, {tiingoCount} Tiingo.){momentumNote}",
                cfg.CatalystDetectionEnabled ? parsed.Catalyst : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude sentiment analysis failed for {Symbol} — treating as unavailable", symbol);
            return (null, "Sentiment analysis unavailable.", null);
        }
    }

    // Best-effort: an archive read failure just means no momentum tilt today,
    // never a failed research run.
    private async Task<SentimentMomentum.Result> BlendSentimentMomentumAsync(
        string symbol, decimal level, CancellationToken ct)
    {
        var cfg = researchConfig.Value;
        try
        {
            var prior = await sentimentArchive.GetRecentScoresAsync(
                symbol, DateOnly.FromDateTime(DateTime.UtcNow), cfg.SentimentMomentumLookbackDays, ct);
            return SentimentMomentum.Blend(
                level, prior.Select(p => p.Score).ToList(),
                cfg.SentimentMomentumWeight, cfg.SentimentMomentumMinHistory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentiment momentum lookup failed for {Symbol} — using raw level", symbol);
            return new SentimentMomentum.Result(Math.Clamp(level, -1m, 1m), null, 0);
        }
    }

    // The proprietary sentiment archive (edge-plan Phase 4): daily score +
    // the article metadata behind it. Strictly best-effort - an archive
    // failure logs and never fails the research run; the unique (Symbol,
    // Date) index makes a second account's same-day run a no-op.
    private async Task ArchiveSentimentAsync(
        string symbol, decimal score, IReadOnlyList<NewsArticle> articles, CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await sentimentArchive.SaveDailyScoreAsync(new SentimentDailyScore
            {
                Symbol = symbol,
                Date = today,
                Score = score,
                ArticleCount = articles.Count,
                Model = claudeConfig.Value.Model,
            }, ct);
            await sentimentArchive.SaveArticlesAsync(articles.Select(a => new SentimentArticle
            {
                Symbol = symbol,
                Date = today,
                Source = a.Source,
                Title = a.Title.Length > 500 ? a.Title[..500] : a.Title,
                Url = a.Url is { Length: > 2000 } ? a.Url[..2000] : a.Url,
                PublishedAtUtc = a.PublishedAtUtc,
                Description = a.Summary is { Length: > 1000 } ? a.Summary[..1000] : a.Summary,
            }).ToList(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentiment archive write failed for {Symbol} — research continues", symbol);
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
        IndicatorResult ind, decimal? sentimentScore, SetupType setup, decimal? previousHistogram) =>
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

    // Bounded conviction adjustment for a Claude-detected forward catalyst,
    // mirroring the post-earnings adjustment above. Internal static (pure)
    // so tests can exercise the clamping and rejection rules directly. The
    // caller passes null when detection is disabled or nothing was found.
    internal decimal ApplyCatalystAdjustment(decimal conviction, ClaudeCatalystResult? catalyst, out string? reasoning)
    {
        var cfg = researchConfig.Value;
        return ApplyCatalystAdjustment(conviction, catalyst, cfg.MaxCatalystBoost, cfg.MaxCatalystPenalty,
            cfg.CatalystMaxDaysAhead, DateOnly.FromDateTime(DateTime.UtcNow), out reasoning);
    }

    internal static decimal ApplyCatalystAdjustment(
        decimal conviction, ClaudeCatalystResult? catalyst,
        decimal maxBoost, decimal maxPenalty, int maxDaysAhead, DateOnly today, out string? reasoning)
    {
        reasoning = null;
        if (catalyst is not { Detected: true }) return conviction;

        // Defensive rejections: earnings are the earnings gate's job however
        // the prompt was interpreted, and a "catalyst" that is undated, in
        // the past, or too far out isn't the near-term event this rewards.
        if (catalyst.Type?.Contains("earnings", StringComparison.OrdinalIgnoreCase) == true) return conviction;
        if (catalyst.ExpectedDate is not null)
        {
            if (!DateOnly.TryParse(catalyst.ExpectedDate, out var expected)) return conviction;
            if (expected < today || expected > today.AddDays(maxDaysAhead)) return conviction;
        }

        var strength = Math.Clamp((decimal)catalyst.Strength, 0m, 1m);
        if (strength == 0m) return conviction;

        var dateNote = catalyst.ExpectedDate is null ? "" : $" ~{catalyst.ExpectedDate}";
        if (string.Equals(catalyst.Direction, "bearish", StringComparison.OrdinalIgnoreCase))
        {
            var penalty = strength * maxPenalty;
            reasoning = $" Bearish catalyst ({catalyst.Type}{dateNote}) reduced by {penalty:F1} pts.";
            return Math.Max(conviction - penalty, 0.0m);
        }

        var boost = strength * maxBoost;
        reasoning = $" Upcoming catalyst ({catalyst.Type}{dateNote}) added {boost:F1} pts.";
        return Math.Min(conviction + boost, 10.0m);
    }

    private async Task<Recommendation> DetermineRecommendationAsync(
        int accountId, TradingMode tradingMode, string symbol, IndicatorResult ind, decimal conviction, StrategyWeights weights,
        SetupType setupType)
    {
        var openTrades = await tradeRepo.GetOpenTradesAsync(accountId, tradingMode);
        if (openTrades.Any(t => t.Symbol == symbol))
            return Recommendation.Hold;

        if (ind.Rsi14 > 75)
            return Recommendation.Avoid;

        // Breakout setups are capped at Watch - never Buy. Backtested Oct 2023 -
        // Jul 2026 (446-trade baseline): Breakout trades averaged -1.20% (37%
        // win rate) and were the single drag flipping the whole system negative;
        // excluding them turned -5.2% into +14.1% (PF 0.96 -> 1.12). A penalty
        // sweep showed the effect is dose-responsive and that the breakouts
        // strong enough to survive a conviction penalty did even WORSE (-3.4%)
        // - the pattern is buying exhaustion, and its "best" instances are the
        // most stretched. The signal still gets classified/persisted as
        // Breakout so dashboards and refinement keep learning from it.
        if (setupType == SetupType.Breakout && conviction >= weights.WatchThreshold)
            return Recommendation.Watch;

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
        int accountId, string symbol, string? companyName, StockCandle latest, IndicatorResult ind,
        decimal? sentimentScore, string newsSummary,
        SetupType setupType, decimal conviction, Recommendation recommendation,
        ComponentScores componentScores, MarketRegime? currentRegime, EarningsContext? earningsCtx,
        RelativeStrengthResult? rs = null,
        PriceLevelResult? priceLevel = null, string? earningsReasoning = null,
        FundamentalSnapshot? fundamentalSnapshot = null, FundamentalScore? fundamental = null,
        FunnelScores.FunnelShadow? funnelShadow = null,
        decimal? filingDeltaScore = null, string? filingDeltaSummary = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = (await signalRepo.GetByDateAsync(accountId, today))
            .FirstOrDefault(s => s.Symbol == symbol);

        var signal = existing ?? new StockSignal { AccountId = accountId, Symbol = symbol };
        if (companyName is not null) signal.CompanyName = companyName;

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

        // Store null (not the synthetic neutral 0.5 the scorers substitute
        // into the conviction blend) whenever the underlying input was
        // unavailable - a stored fake 0.5 is indistinguishable from a genuine
        // one and pollutes the Refinement agent's score/outcome correlations.
        signal.RsiScore = ind.Rsi14 is null ? null : componentScores.Rsi;
        signal.MacdScore = ind.MacdHistogram is null ? null : componentScores.Macd;
        signal.VolumeScore = ind.VolumeRatio is null ? null : componentScores.Volume;
        signal.SentimentComponentScore = sentimentScore is null ? null : componentScores.Sentiment;
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
            // InsufficientData returns a synthetic 0.5 for the conviction
            // blend - don't persist that as if it were a computed score.
            signal.PriceLevelScore = priceLevel.Context == PriceLevelContext.InsufficientData
                ? null
                : priceLevel.Score;
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

        if (funnelShadow is not null)
        {
            signal.GateScore = funnelShadow.GateScore;
            signal.ForwardScore = funnelShadow.ForwardScore;
            signal.ForwardScoreDegraded = funnelShadow.ForwardScoreDegraded;
            signal.WouldPassGate = funnelShadow.WouldPassGate;
            signal.WouldBeVetoed = funnelShadow.WouldBeVetoed;
        }

        // Filing-delta shadow (FD1) - overwritten (not preserved) on rescore
        // so the stored value always reflects the decay as of the latest pass.
        signal.FilingDeltaScore = filingDeltaScore;
        signal.FilingDeltaSummary = filingDeltaSummary;

        if (existing is null)
            return await signalRepo.AddAsync(signal);

        await signalRepo.UpdateAsync(signal);
        return signal;
    }
}
