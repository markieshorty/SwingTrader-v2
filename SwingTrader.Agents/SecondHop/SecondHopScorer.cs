using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.SecondHop;

public interface ISecondHopScorer
{
    // The per-symbol relevance pass (docs/second-hop-plan #3): null when the
    // symbol has no usable links or no qualifying linked-company events -
    // degraded semantics identical to the other forward inputs.
    Task<(decimal Score, string Summary)?> ScoreAsync(
        IClaudeClient claude, string symbol, string? companyName, CancellationToken ct = default);
}

// Propagates scored events at economically LINKED companies to a watchlist
// target. Sources are the sentiment archive (watchlist symbols + the
// bellwether set) - no ad-hoc news fetching (spec decision #2). Events
// directly about the target are excluded: this score measures the second hop
// only; the target's own news is stage-2's job (spec decision #3).
public class SecondHopScorer(
    IEconomicLinkRepository linkRepo,
    ISentimentArchiveRepository sentimentArchive,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<SecondHopConfig> config,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<SecondHopScorer> logger) : ISecondHopScorer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<(decimal Score, string Summary)?> ScoreAsync(
        IClaudeClient claude, string symbol, string? companyName, CancellationToken ct = default)
    {
        var cfg = config.Value;
        if (!cfg.Enabled) return null;

        var links = (await linkRepo.GetLinksAsync(symbol, ct))
            .Where(l => !l.Suppressed && l.LinkedTicker is not null && l.LinkedTicker != symbol)
            .ToList();
        if (links.Count == 0) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-cfg.LookbackDays);
        var linkedTickers = links.Select(l => l.LinkedTicker!).Distinct().ToList();

        var events = SelectStrongestPerSymbol(
            await sentimentArchive.GetScoresForSymbolsSinceAsync(linkedTickers, from, ct),
            cfg.MinSourceMagnitude);
        if (events.Count == 0) return null;

        var linkLines = string.Join("\n", links.Select(l =>
            $"- {l.LinkedTicker} ({l.LinkedName}): {l.Relation}, strength {l.Strength:0.0}. Transmission: {l.TransmissionNote}"));
        var eventLines = string.Join("\n", events.Select((e, i) =>
            $"[{i}] {e.Symbol} on {e.Date:yyyy-MM-dd}: archived news-sentiment score {e.Score:+0.00;-0.00}"));

        var systemPrompt =
            "You are an equity analyst assessing how news at economically linked companies transmits to a " +
            "target stock. Only specific commercial transmission counts - sector sympathy is NOT transmission. " +
            "Respond only with valid JSON.";

        var userPrompt =
            $"Target stock: {symbol}{(companyName is null ? "" : $" ({companyName})")}.\n\n" +
            $"Known economic links:\n{linkLines}\n\n" +
            $"Recent scored news events at linked companies:\n{eventLines}\n\n" +
            "For each event, judge whether it plausibly TRANSMITS to the target through the stated link, and " +
            "with what direction and strength FOR THE TARGET (a competitor's good news may be bearish; a " +
            "supplier's capacity problem may be bearish even though it was scored on that company's own terms).\n\n" +
            "Respond with this exact JSON structure:\n" +
            "{\n" +
            "  \"events\": [ { \"index\": <int>, \"transmits\": <bool>, \"impact\": <float -1.0 to 1.0 for the TARGET, 0 if transmits is false> } ],\n" +
            "  \"summary\": \"<one or two sentences naming the strongest transmission and why, or 'no meaningful transmission'>\"\n" +
            "}\n\n" +
            "Rules: exclude any event that is actually ABOUT the target itself. Be conservative - most news does " +
            "not transmit; only clear, link-specific effects deserve non-zero impact.";

        await claudeRateLimiter.WaitAsync(ct);
        var response = await claude.SendMessageAsync(new ClaudeRequest(
            // Deliberately NOT PremiumModel: this runs once per watchlist
            // symbol EVERY DAY during Research (same cardinality as
            // sentiment scoring), not the low-frequency synthesis Opus is
            // reserved for - routing a daily per-symbol call to Opus would
            // multiply cost every day, not just once. Correction of a
            // mis-classification made 13 Jul 2026.
            claudeConfig.Value.Model, claudeConfig.Value.MaxTokens, systemPrompt,
            [new ClaudeMessage("user", userPrompt)]));
        var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        try
        {
            var transmitted = ParseTransmissions(raw)
                .Where(t => t.Transmits && t.Impact != 0m && t.Index >= 0 && t.Index < events.Count)
                .Select(t => new SecondHopMath.TransmittedEvent(t.Impact, events[t.Index].Date))
                .ToList();
            if (transmitted.Count == 0) return null;

            var score = SecondHopMath.Combine(transmitted, today, cfg.HalfLifeTradingDays);
            var summary = ParseSummary(raw);
            return (score, summary);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Second-hop relevance parse failed for {Symbol} — score omitted", symbol);
            return null;
        }
    }

    // One event per linked symbol: a story that dominates a symbol's news for
    // a week produces one archive row per DAY, and offering each row as a
    // separate "event" counted the same story several times over (the decay +
    // clamp only partially masked it). The strongest day carries the story;
    // the per-symbol cap also bounds the prompt without a magic Take(N).
    internal static List<SentimentDailyScore> SelectStrongestPerSymbol(
        IEnumerable<SentimentDailyScore> scores, decimal minMagnitude) =>
        scores
            .Where(e => Math.Abs(e.Score) >= minMagnitude)
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => Math.Abs(e.Score)).ThenByDescending(e => e.Date).First())
            .OrderByDescending(e => Math.Abs(e.Score))
            .ToList();

    // Internal static so the clamping/filtering rules are directly testable.
    internal static List<(int Index, bool Transmits, decimal Impact)> ParseTransmissions(string raw)
    {
        var parsed = JsonSerializer.Deserialize<RelevanceResponse>(StripFences(raw), JsonOpts)
            ?? throw new JsonException("null second-hop result");
        return (parsed.Events ?? [])
            .Select(e => (e.Index, e.Transmits, Math.Clamp((decimal)e.Impact, -1m, 1m)))
            .ToList();
    }

    internal static string ParseSummary(string raw)
    {
        var parsed = JsonSerializer.Deserialize<RelevanceResponse>(StripFences(raw), JsonOpts);
        return string.IsNullOrWhiteSpace(parsed?.Summary) ? "Second-hop transmission detected." : parsed.Summary.Trim();
    }

    private static string StripFences(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```")) return text;
        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0) text = text[(firstNewline + 1)..];
        if (text.EndsWith("```")) text = text[..^3];
        return text.Trim();
    }

    private sealed record RelevanceResponse(List<RelevanceEvent>? Events, string? Summary);
    private sealed record RelevanceEvent(int Index, bool Transmits, double Impact);
}
