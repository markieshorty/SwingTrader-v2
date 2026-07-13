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
            candidates.Select(c => $"{c.Symbol} | {c.CompanyName} | {c.ChangePercent:+0.00;-0.00}% | {c.Volume:N0} | {(c.IsTopMover ? "\U0001F525 TOP MOVER" : "")}"));

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
            $"Candidates (symbol | name | change% | volume | flag):\n{candidateLines}\n\n" +
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
            var request = new ClaudeRequest(
                claudeConfig.Value.WatchlistModel ?? claudeConfig.Value.PremiumModel,
                claudeConfig.Value.MaxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            await claudeRateLimiter.WaitAsync(ct);
            var response = await claude.SendMessageAsync(request);
            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
            var text = StripCodeFences(raw);

            var parsed = JsonSerializer.Deserialize<ClaudeWatchlistResponse>(text, JsonOpts);
            if (parsed?.Selected == null)
                throw new JsonException("null or missing 'selected' array");

            if (parsed.Selected.Count != target)
                logger.LogWarning("Claude returned {Count} selections instead of {Target}", parsed.Selected.Count, target);

            logger.LogInformation("Market commentary: {Commentary}", parsed.MarketCommentary);

            return parsed.Selected.Select(s => new WatchlistSelection(
                s.Symbol.ToUpperInvariant(),
                s.CompanyName,
                s.Sector,
                s.Reason
            )).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Claude watchlist selection failed");
            return null;
        }
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
