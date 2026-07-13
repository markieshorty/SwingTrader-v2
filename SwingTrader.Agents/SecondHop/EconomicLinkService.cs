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

public interface IEconomicLinkService
{
    // Rebuild link graphs for whichever of these symbols are missing or
    // stale. Rides the weekly Watchlist job; best-effort per symbol.
    Task<int> RefreshStaleLinksAsync(IReadOnlyCollection<string> symbols, CancellationToken ct = default);
}

// Builds the second-hop economic graph (docs/second-hop-plan #1): Claude
// lists a symbol's most economically significant links with a transmission
// note and a one-line rationale each. Deliberately human-auditable - the
// rationale is required and the UI can suppress any link; hallucinated links
// must be cheap for a human to spot and kill, not silently trusted.
public class EconomicLinkService(
    IEconomicLinkRepository linkRepo,
    IUserHttpClientFactory clientFactory,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<SecondHopConfig> config,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<EconomicLinkService> logger) : IEconomicLinkService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string[] ValidRelations = ["Supplier", "Customer", "Competitor", "SharedChain"];

    public async Task<int> RefreshStaleLinksAsync(IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        if (!config.Value.Enabled || symbols.Count == 0) return 0;

        var stale = await linkRepo.GetStaleSymbolsAsync(
            symbols, TimeSpan.FromDays(config.Value.GraphMaxAgeDays), ct);
        if (stale.Count == 0) return 0;

        var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(SwingTraderDbContext.SystemAccountId, ct);

        var refreshed = 0;
        foreach (var symbol in stale)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var links = await BuildLinksAsync(claude, symbol, ct);
                await linkRepo.ReplaceLinksAsync(symbol, links, ct);
                refreshed++;
                logger.LogInformation("Economic links rebuilt for {Symbol}: {Count} links", symbol, links.Count);
            }
            catch (Exception ex)
            {
                // A failed build keeps the previous (stale) graph - stale
                // links beat no links, and next week retries.
                logger.LogWarning(ex, "Economic-link build failed for {Symbol} — keeping previous graph", symbol);
            }
        }
        return refreshed;
    }

    private async Task<List<EconomicLink>> BuildLinksAsync(IClaudeClient claude, string symbol, CancellationToken ct)
    {
        var systemPrompt =
            "You are an equity supply-chain analyst mapping economic relationships between public companies. " +
            "Only include relationships you are confident are real and currently material. Respond only with valid JSON.";

        var userPrompt =
            $"List the 5-10 most economically significant links for the US-listed stock {symbol}: its key " +
            "suppliers, major customers, direct competitors, and shared-supply-chain names whose news " +
            "materially transmits to it.\n\n" +
            "Respond with this exact JSON structure:\n" +
            "{\n" +
            "  \"links\": [\n" +
            "    {\n" +
            "      \"name\": \"<company name>\",\n" +
            "      \"ticker\": \"<US ticker, or null if private/foreign-unlisted>\",\n" +
            "      \"relation\": \"<Supplier | Customer | Competitor | SharedChain>\",\n" +
            "      \"transmission\": \"<one line: which direction news flows, e.g. 'strong results at this customer are bullish for " + "the target'>\",\n" +
            "      \"strength\": <float 0.0-1.0, how strongly linked-company news moves the target>,\n" +
            "      \"rationale\": \"<one line: WHY this link is real and material - be specific>\"\n" +
            "    }\n" +
            "  ]\n" +
            "}\n\n" +
            "Rules: quality over quantity - omit anything speculative. Sector sympathy alone is NOT a link; " +
            "name the specific commercial relationship.";

        await claudeRateLimiter.WaitAsync(ct);
        var response = await claude.SendMessageAsync(new ClaudeRequest(
            claudeConfig.Value.PremiumModel, claudeConfig.Value.MaxTokens, systemPrompt,
            [new ClaudeMessage("user", userPrompt)]));
        var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        return ParseLinks(raw, symbol, claudeConfig.Value.PremiumModel);
    }

    // Internal static so the defensive rules are directly testable: relation
    // whitelist, strength clamp, self-links dropped, rationale required.
    internal static List<EconomicLink> ParseLinks(string raw, string symbol, string model)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        var parsed = JsonSerializer.Deserialize<LinksResponse>(text, JsonOpts)
            ?? throw new JsonException("null economic-links result");

        return (parsed.Links ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l.Name)
                && !string.IsNullOrWhiteSpace(l.Rationale)
                && ValidRelations.Contains(l.Relation, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(l.Ticker, symbol, StringComparison.OrdinalIgnoreCase)) // no self-links
            .Take(10)
            .Select(l => new EconomicLink
            {
                Symbol = symbol,
                LinkedName = l.Name.Trim(),
                LinkedTicker = string.IsNullOrWhiteSpace(l.Ticker) ? null : l.Ticker.Trim().ToUpperInvariant(),
                Relation = ValidRelations.First(r => r.Equals(l.Relation, StringComparison.OrdinalIgnoreCase)),
                TransmissionNote = (l.Transmission ?? "").Trim(),
                Strength = Math.Clamp((decimal)l.Strength, 0m, 1m),
                Rationale = l.Rationale.Trim(),
                Model = model,
            })
            .ToList();
    }

    private sealed record LinksResponse(List<LinkEntry>? Links);
    private sealed record LinkEntry(string Name, string? Ticker, string Relation, string? Transmission, double Strength, string Rationale);
}
