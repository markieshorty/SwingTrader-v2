using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.SecondHop;

public record BellwetherSyncResult(bool Configured, int Scored, int NoNews, int AlreadyScored, int Failed, string Summary);

public interface IBellwetherSyncService
{
    Task<BellwetherSyncResult> SyncAsync(CancellationToken ct = default);
}

// Daily platform job (docs/second-hop-plan #2): fetch + Claude-score news for
// the fixed bellwether set into the existing SentimentDailyScore archive -
// second-hop events mostly originate at large names that may sit on no
// watchlist. The (symbol, day) unique index makes re-runs free, and symbols
// already scored today (e.g. a bellwether that IS on a watchlist and was
// researched this morning) are skipped without any API call.
public class BellwetherSyncService(
    ISentimentArchiveRepository sentimentArchive,
    IWatchlistRepository watchlists,
    IUserHttpClientFactory clientFactory,
    IFinnhubRateLimiter finnhubRateLimiter,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<SecondHopConfig> config,
    IOptions<ResearchConfig> researchConfig,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<BellwetherSyncService> logger) : IBellwetherSyncService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<BellwetherSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var cfg = config.Value;
        if (!cfg.Enabled)
            return new BellwetherSyncResult(false, 0, 0, 0, 0, "BellwetherSync disabled (SecondHop:Enabled=false).");
        if (cfg.Bellwethers.Count == 0)
            return new BellwetherSyncResult(true, 0, 0, 0, 0, "No bellwethers configured.");

        var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(SwingTraderDbContext.SystemAccountId, ct);
        var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(SwingTraderDbContext.SystemAccountId, ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // One read answers "who already has today's score" for the whole set.
        var alreadyToday = (await sentimentArchive.GetScoresForSymbolsSinceAsync(cfg.Bellwethers, today, ct))
            .Select(s => s.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A bellwether that is ALSO a watchlist symbol belongs to research:
        // its 7:30 run writes a richer momentum-blended score, and the
        // (symbol, day) unique index means whoever writes first wins - this
        // job writing the lean level first would silently block the good row.
        var watchlistOwned = (await watchlists.GetActiveSymbolsAcrossAccountsAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lookbackDays = researchConfig.Value.NewsLookbackDays;
        int scored = 0, noNews = 0, skipped = 0, failed = 0;

        foreach (var symbol in cfg.Bellwethers)
        {
            if (ct.IsCancellationRequested) break;
            if (alreadyToday.Contains(symbol) || watchlistOwned.Contains(symbol)) { skipped++; continue; }

            try
            {
                await finnhubRateLimiter.WaitAsync(ct);
                var news = await finnhub.GetCompanyNewsAsync(
                    symbol,
                    DateTime.UtcNow.AddDays(-lookbackDays).ToString("yyyy-MM-dd"),
                    DateTime.UtcNow.ToString("yyyy-MM-dd"));

                var headlines = news
                    .OrderByDescending(n => n.Datetime)
                    .Take(researchConfig.Value.MaxNewsArticles)
                    .Select(n => $"Headline: {n.Headline}\nSummary: {n.Summary}")
                    .ToList();

                if (headlines.Count == 0)
                {
                    // "No news" is a genuine neutral, same convention as research.
                    await SaveAsync(symbol, today, 0m, ct);
                    noNews++;
                    continue;
                }

                var score = await ScoreAsync(claude, symbol, headlines, ct);
                await SaveAsync(symbol, today, score, ct);
                scored++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Bellwether sync failed for {Symbol} — continuing", symbol);
            }
        }

        var summary = $"BellwetherSync: {scored} scored, {noNews} no-news, {skipped} already scored today, {failed} failed ({cfg.Bellwethers.Count} bellwethers).";
        logger.LogInformation("{Summary}", summary);
        return new BellwetherSyncResult(true, scored, noNews, skipped, failed, summary);
    }

    private async Task<decimal> ScoreAsync(IClaudeClient claude, string symbol, List<string> headlines, CancellationToken ct)
    {
        // Deliberately leaner than research's sentiment call (no catalyst, no
        // summary text): the archive row only needs the LEVEL - the relevance
        // pass reasons about transmission itself.
        var userPrompt =
            $"Score the overall news sentiment for stock ticker {symbol} from these articles.\n\n" +
            string.Join("\n---\n", headlines) + "\n\n" +
            "Respond with this exact JSON structure: {\"sentiment_score\": <float between -1.0 and 1.0>}\n" +
            "-1.0 = extremely negative (fraud, disaster), 0.0 = neutral/mixed, +1.0 = extremely positive (major breakthrough).";

        await claudeRateLimiter.WaitAsync(ct);
        var response = await claude.SendMessageAsync(new ClaudeRequest(
            claudeConfig.Value.Model, claudeConfig.Value.MaxTokens,
            "You are a financial sentiment analyst. Respond only with valid JSON.",
            [new ClaudeMessage("user", userPrompt)]));
        var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        return ParseScore(raw);
    }

    internal static decimal ParseScore(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }
        var parsed = JsonSerializer.Deserialize<ScoreResponse>(text, JsonOpts)
            ?? throw new JsonException("null bellwether score");
        return Math.Clamp((decimal)parsed.SentimentScore, -1m, 1m);
    }

    private Task SaveAsync(string symbol, DateOnly date, decimal score, CancellationToken ct) =>
        sentimentArchive.SaveDailyScoreAsync(new SentimentDailyScore
        {
            Symbol = symbol,
            Date = date,
            Score = score,
            ArticleCount = 0, // bellwether rows are level-only; article metadata isn't archived for them
            Model = claudeConfig.Value.Model,
        }, ct);

    private sealed record ScoreResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("sentiment_score")] double SentimentScore);
}
