using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.Watchlist;

public class WatchlistSelectionService(
    IOptions<ClaudeConfig> claudeConfig,
    IClaudeRateLimiter claudeRateLimiter,
    ILogger<WatchlistSelectionService> logger) : IWatchlistSelectionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<List<WatchlistSelection>?> SelectAsync(
        IClaudeClient claude,
        List<ScreenedCandidate> candidates,
        decimal spyChangePercent,
        decimal vix,
        int targetWatchlistSize,
        CancellationToken ct = default)
    {
        var target = targetWatchlistSize;
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var spyDir = spyChangePercent >= 0 ? "up" : "down";
        var fearLevel = vix switch { < 15 => "Low fear", < 20 => "Moderate", < 30 => "Elevated", _ => "High fear" };

        var candidateLines = string.Join("\n",
            candidates.Select(c => $"{c.Symbol} | {c.CompanyName} | {c.ChangePercent:+0.00;-0.00}% | {c.Volume:N0} | " +
                $"{(c.SelectionPercentile is { } p ? $"rank {p:0}/100" : "")} | {(c.IsTopMover ? "\U0001F525 TOP MOVER" : "")}"));

        var systemPrompt =
            "You are an expert swing trader and portfolio manager selecting stocks to monitor for the coming week. " +
            "You are selecting candidates for a swing trading system that holds positions for 2-10 days targeting 8-12% gains. " +
            "Candidates marked \U0001F525 TOP MOVER are currently showing unusual market activity. These may represent " +
            "short-term opportunities but also carry higher noise risk. Consider them alongside but not above technical quality. " +
            "Respond only with valid JSON. No explanation outside the JSON structure.";

        var userPrompt =
            $"Today is {date}. Select exactly {target} stocks from the candidates below for swing trading this week.\n\n" +
            "Selection criteria to consider:\n" +
            "- Sector diversity (no more than 5 from any sector)\n" +
            "- Momentum characteristics suitable for 2-10 day holds\n" +
            "- Avoid heavily news-driven spikes that typically reverse\n" +
            "- Prefer stocks showing technical setup potential (meaningful move with volume confirmation)\n" +
            "- Consider current market conditions\n\n" +
            "Current market context:\n" +
            $"- Day's overall market direction: {spyDir} ({spyChangePercent:+0.00;-0.00}%)\n" +
            $"- VIX level: {vix:0.00} ({fearLevel})\n\n" +
            "The rank column is a cross-sectional percentile vs today's whole screened universe " +
            "(blend of momentum magnitude and dollar volume; 100 = strongest). Treat it as one input, not a verdict.\n\n" +
            $"Candidates (symbol | name | change% | volume | rank | flag):\n{candidateLines}\n\n" +
            "Respond with this exact JSON:\n" +
            "{\n" +
            "  \"selected\": [\n" +
            "    {\n" +
            "      \"symbol\": \"TICKER\",\n" +
            "      \"company_name\": \"Full Company Name\",\n" +
            "      \"sector\": \"Technology\",\n" +
            "      \"reason\": \"One sentence on why selected\"\n" +
            "    }\n" +
            "  ],\n" +
            "  \"market_commentary\": \"2-3 sentences on current market conditions and this week's opportunity set\"\n" +
            "}";

        try
        {
            // Budget = JSON answer (~120-170 tokens per pick; a 50-pick list
            // at the flat 4,000-token cap came back truncated, 20 Jul 2026)
            // PLUS very generous adaptive-thinking headroom. Thinking shares
            // max_tokens with the answer and the observed run spent 9,500
            // tokens purely thinking, so an answer-sized budget returns no
            // text at all. Reasoning is deliberately kept ON - the weekly
            // shortlist is a judgment call worth the extra ~$0.2/run.
            var maxTokens = Math.Max(claudeConfig.Value.MaxTokens, target * 170 + 1000) + 30000;
            var request = new ClaudeRequest(
                claudeConfig.Value.WatchlistModel ?? claudeConfig.Value.PremiumModel,
                maxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            // 20 Jul 2026: a run came back HTTP 200 with NO text block at all
            // ("input does not contain any JSON tokens") and the old code threw
            // away the response shape, leaving nothing to diagnose. Log the
            // shape and retry once - an empty body has so far been transient.
            var raw = string.Empty;
            for (var attempt = 1; attempt <= 2 && raw.Length == 0; attempt++)
            {
                await claudeRateLimiter.WaitAsync(ct);
                var response = await claude.SendMessageAsync(request);
                raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
                if (raw.Length == 0)
                    logger.LogWarning(
                        "Claude watchlist selection returned no text (attempt {Attempt}): stop_reason={StopReason}, blocks=[{Blocks}], output_tokens={OutputTokens}",
                        attempt, response.StopReason,
                        string.Join(",", response.Content.Select(c => c.Type)),
                        response.Usage?.OutputTokens);
            }
            var text = ClaudeJson.Extract(raw);

            var parsed = JsonSerializer.Deserialize<ClaudeWatchlistResponse>(text, JsonOpts);
            if (parsed?.Selected == null)
                throw new JsonException("null or missing 'selected' array");

            if (parsed.Selected.Count != target)
                logger.LogWarning("Claude returned {Count} selections instead of {Target}", parsed.Selected.Count, target);

            logger.LogInformation("Market commentary: {Commentary}", parsed.MarketCommentary);

            var percentileBySymbol = candidates
                .GroupBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SelectionPercentile, StringComparer.OrdinalIgnoreCase);

            return parsed.Selected.Select(s => new WatchlistSelection(
                s.Symbol.ToUpperInvariant(),
                s.CompanyName,
                s.Sector,
                s.Reason,
                percentileBySymbol.GetValueOrDefault(s.Symbol.ToUpperInvariant())
            )).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Claude watchlist selection failed");
            return null;
        }
    }


    private record ClaudeSelectedItem(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("company_name")] string CompanyName,
        [property: JsonPropertyName("sector")] string Sector,
        [property: JsonPropertyName("reason")] string Reason
    );

    private record ClaudeWatchlistResponse(
        [property: JsonPropertyName("selected")] List<ClaudeSelectedItem> Selected,
        [property: JsonPropertyName("market_commentary")] string MarketCommentary
    );
}
