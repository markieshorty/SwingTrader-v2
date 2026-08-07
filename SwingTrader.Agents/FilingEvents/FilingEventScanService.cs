using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Agents;
using SwingTrader.Agents.Filings;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Edgar;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.FilingEvents;

public record FilingEventScanResult(bool Enabled, int Scanned, int Routed, int Classified, int Failed, string Summary);

public interface IFilingEventScanService
{
    Task<FilingEventScanResult> ScanAsync(DateOnly date, CancellationToken ct = default);
}

// Small-cap filing events P1 (docs/filing-events-plan): once per trading day,
// pull EVERY 8-K filed market-wide, route mechanically by item code (zero
// tokens), keep the neglected-company subset, and have Claude classify only
// the routed few into a typed event feed. Observation only - nothing trades
// on these until the P2 forward scorecard earns it.
public class FilingEventScanService(
    IEdgarClient edgar,
    IFilingEventRepository events,
    Infrastructure.Market.IMarketUniverseService universe,
    IUserHttpClientFactory clientFactory,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<FilingEventsConfig> config,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<FilingEventScanService> logger) : IFilingEventScanService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Every KNOWN routable item code -> event type. Which subset actually
    // routes is config (FilingEvents:RoutedItemCodes); the default is ALL
    // seven (6 Aug 2026: widened from the lean four when the model moved to
    // Haiku - same ~£3-7/month budget now covers the BULLISH stream too:
    // agreements/deals are where the long-book hypotheses live). Everything
    // else - earnings releases, Reg FD, votes - never spends a token.
    internal static readonly IReadOnlyDictionary<string, string> KnownItems = new Dictionary<string, string>
    {
        ["4.02"] = "NonReliance",
        ["5.02"] = "OfficerChange",
        ["3.01"] = "ListingDeficiency",
        ["1.03"] = "Bankruptcy",
        ["2.01"] = "AcquisitionDisposition",
        ["1.01"] = "MaterialAgreement",
        ["1.02"] = "AgreementTermination",
    };

    internal static readonly string[] DefaultCodes = ["4.02", "5.02", "3.01", "1.03", "2.01", "1.01", "1.02"];

    // True when the filing's items include an enabled routable code.
    // Internal static for tests. Item strings arrive both bare ("4.02") and
    // prefixed ("Item 4.02") depending on the EDGAR surface.
    internal static string? RouteEventType(IReadOnlyList<string> items, IReadOnlyCollection<string>? enabledCodes = null)
    {
        var enabled = enabledCodes is { Count: > 0 } ? enabledCodes : DefaultCodes;
        foreach (var raw in items)
        {
            var code = raw.Replace("Item", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (enabled.Contains(code, StringComparer.Ordinal) && KnownItems.TryGetValue(code, out var type))
                return type;
        }
        return null;
    }

    // Internal static for tests. Empty SIC means EDGAR gave us none, which is
    // not grounds for exclusion on its own.
    internal static bool IsExcludedIndustry(string? sic, IReadOnlyCollection<string>? excluded)
    {
        if (string.IsNullOrWhiteSpace(sic)) return false;
        var codes = excluded is { Count: > 0 } ? excluded : DefaultExcludedSics;
        return codes.Contains(sic.Trim(), StringComparer.Ordinal);
    }

    // 6770 "Blank Checks" - SPAC shells.
    internal static readonly string[] DefaultExcludedSics = ["6770"];

    public async Task<FilingEventScanResult> ScanAsync(DateOnly date, CancellationToken ct = default)
    {
        var cfg = config.Value;
        if (!cfg.Enabled)
            return new FilingEventScanResult(false, 0, 0, 0, 0, "Filing events disabled (FilingEvents:Enabled=false).");

        var filings = await edgar.SearchEightKsAsync(date, ct);

        // EDGAR accepts filings 06:00-22:00 ET, so a run before the day has
        // opened (a manual scan in the small hours, a Monday morning, the
        // day after a holiday) legitimately finds nothing. Fall back one day
        // rather than reporting an empty scan - the accession-number dedup
        // makes re-reading a day already scanned free.
        if (filings.Count == 0)
        {
            date = date.AddDays(-1);
            filings = await edgar.SearchEightKsAsync(date, ct);
            logger.LogInformation("Filing events: no filings for the requested day - fell back to {Date}", date);
        }

        // Cheap pre-filter only: anything in the liquid ~1,500 universe is
        // certainly well-covered, so it never justifies a float lookup. It is
        // NOT the size test - on its own it let Yum China ($16.5bn) and
        // iRhythm ($4.9bn) into the feed, which is why the float gate below
        // exists (7 Aug 2026).
        var covered = new HashSet<string>(await universe.GetUniverseAsync(ct), StringComparer.OrdinalIgnoreCase);

        // Public float per company, resolved lazily and cached for the scan.
        // Many filers file repeatedly, and float only restates annually.
        var floatByCik = new Dictionary<string, decimal?>(StringComparer.Ordinal);

        IClaudeClient? claude = null;
        int routed = 0, classified = 0, failed = 0, tooBig = 0, shells = 0, capUnknown = 0;
        foreach (var filing in filings)
        {
            ct.ThrowIfCancellationRequested();
            if (classified >= cfg.MaxClassificationsPerDay) break; // hard token-budget lid

            if (filing.Ticker.Length == 0) continue;               // funds/co-filers
            if (covered.Contains(filing.Ticker)) continue;         // certainly covered
            var eventType = RouteEventType(filing.Items, cfg.RoutedItemCodes);
            if (eventType is null) continue;
            if (await events.ExistsAsync(filing.AccessionNumber, ct)) continue;

            // Blank-cheque shells (SIC 6770) pass a float test comfortably but
            // their 8-K flow is deal mechanics, not company fundamentals. The
            // SIC rides in the search hit, so this costs nothing.
            if (IsExcludedIndustry(filing.Sic, cfg.ExcludedSicCodes)) { shells++; continue; }

            // The real size test. Public float is the SEC's own basis for
            // "smaller reporting company", and the CIK is already in hand.
            if (!floatByCik.TryGetValue(filing.Cik, out var publicFloat))
            {
                publicFloat = await edgar.GetPublicFloatAsync(filing.Cik, ct);
                floatByCik[filing.Cik] = publicFloat;
            }
            if (publicFloat is null)
            {
                // Unknown size is excluded rather than guessed - an unmeasured
                // name would silently widen the population the hypotheses are
                // declared over.
                capUnknown++;
                continue;
            }
            if (publicFloat > cfg.MaxPublicFloatUsd) { tooBig++; continue; }

            routed++;

            var evt = new FilingEvent
            {
                // Platform-level row, same as the other shared tables. Without
                // this the FK to Accounts rejects the insert (AccountId 0),
                // which is what silently killed every scan on 7 Aug 2026.
                AccountId = SwingTraderDbContext.SystemAccountId,
                Symbol = filing.Ticker,
                CompanyName = filing.CompanyName,
                Cik = filing.Cik,
                AccessionNumber = filing.AccessionNumber,
                MarketCapUsd = publicFloat,
                FiledAt = filing.FiledAt,
                ItemCodes = string.Join(",", filing.Items),
                EventType = eventType,
                DocumentUrl = $"https://www.sec.gov/Archives/edgar/data/{filing.Cik.TrimStart('0')}/{filing.AccessionNumber.Replace("-", "")}/{filing.PrimaryDocument}",
            };

            try
            {
                var html = await edgar.GetDocumentAsync(filing.Cik, filing.AccessionNumber, filing.PrimaryDocument, ct);
                var text = FilingTextExtractor.HtmlToText(html);
                claude ??= await clientFactory.CreateClaudeAsync<IClaudeClient>(SwingTraderDbContext.SystemAccountId, ct);
                var (direction, severity, summary, facts) = await ClassifyAsync(
                    claude, cfg.Model ?? claudeConfig.Value.Model, evt, Truncate(text, 12_000), ct);
                evt.Direction = direction;
                evt.Severity = severity;
                evt.Summary = summary;
                evt.Facts = facts;
                classified++;
            }
            catch (Exception ex)
            {
                // Stored anyway: the item-code routing alone is informative,
                // and the row prevents a re-classify storm on retries.
                failed++;
                logger.LogWarning(ex, "Filing-event classification failed for {Symbol} {Accession} — stored unclassified",
                    filing.Ticker, filing.AccessionNumber);
            }

            await events.AddAsync(evt, ct);
        }

        var summaryText =
            $"Filing events {date:yyyy-MM-dd}: {filings.Count} 8-Ks scanned, {routed} routed " +
            $"({classified} classified, {failed} failed); excluded {tooBig} above the " +
            $"${cfg.MaxPublicFloatUsd / 1_000_000m:N0}M float cap, {shells} shells, {capUnknown} unknown float.";
        logger.LogInformation("{Summary}", summaryText);
        return new FilingEventScanResult(true, filings.Count, routed, classified, failed, summaryText);
    }

    private async Task<(string Direction, int Severity, string? Summary, string? Facts)> ClassifyAsync(
        IClaudeClient claude, string model, FilingEvent evt, string text, CancellationToken ct)
    {
        var systemPrompt =
            "You are a forensic small-cap filings analyst reading 8-Ks nobody else reads. " +
            "Respond only with valid JSON.";
        var userPrompt =
            $"{evt.Symbol} ({evt.CompanyName}) filed an 8-K on {evt.FiledAt:yyyy-MM-dd} with item codes [{evt.ItemCodes}] " +
            $"(pre-routed as {evt.EventType}).\n\nFiling text:\n{text}\n\n" +
            "Classify the event. Respond with this exact JSON structure:\n" +
            "{\n" +
            "  \"direction\": \"<Bullish|Bearish|Unclear - the likely effect on the SHARE PRICE over the next month>\",\n" +
            "  \"severity\": <int 1-5: 1 = routine/immaterial, 5 = existential or transformative>,\n" +
            "  \"summary\": \"<2 plain-English sentences: what happened and why it matters>\",\n" +
            "  \"facts\": \"<salient specifics: who departed and stated reason, counterparty and deal size, deficiency and cure deadline - whatever this event type turns on>\"\n" +
            "}\n\n" +
            "Rules: scheduled/routine departures (retirement with orderly succession) are severity 1-2; a CFO exiting " +
            "'to pursue other opportunities' near a filing deadline is not routine. Judge only what is IN the text.";

        await claudeRateLimiter.WaitAsync(ct);
        var response = await claude.SendMessageAsync(new ClaudeRequest(
            model, claudeConfig.Value.MaxTokens + 30000, systemPrompt,
            [new ClaudeMessage("user", userPrompt)]));
        var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
        return ParseClassification(raw);
    }

    // Internal static so parse/clamp rules are directly testable.
    internal static (string Direction, int Severity, string? Summary, string? Facts) ParseClassification(string raw)
    {
        var parsed = JsonSerializer.Deserialize<ClassificationResponse>(ClaudeJson.Extract(raw), JsonOpts)
            ?? throw new JsonException("null filing-event classification");
        var direction = parsed.Direction?.Trim() switch
        {
            "Bullish" or "bullish" => "Bullish",
            "Bearish" or "bearish" => "Bearish",
            _ => "Unclear",
        };
        return (direction, Math.Clamp(parsed.Severity, 1, 5),
            string.IsNullOrWhiteSpace(parsed.Summary) ? null : parsed.Summary.Trim(),
            string.IsNullOrWhiteSpace(parsed.Facts) ? null : parsed.Facts.Trim());
    }

    private sealed record ClassificationResponse(string? Direction, int Severity, string? Summary, string? Facts);

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];
}

// docs/filing-events-plan P1. Enabled default FALSE - flipping it on is an
// explicit spend decision (~£0.30-0.90/day of Sonnet classification).
public class FilingEventsConfig
{
    public bool Enabled { get; set; }

    // The size gate, in USD of public float. $250M is the SEC's own
    // "smaller reporting company" line, so the threshold is a definition
    // rather than a guess. Companies above it, and companies that have never
    // reported a float, never reach classification.
    public decimal MaxPublicFloatUsd { get; set; } = 250_000_000m;

    // SIC codes that never justify classification whatever their size.
    public string[] ExcludedSicCodes { get; set; } = [];
    // Classification is a bounded task (the answer is stated in the text) -
    // Haiku-appropriate, and H-FE3 measures whether its judgments carry
    // information. The subtle-tone work stays with Sonnet in filing-delta.
    public string? Model { get; set; } = "claude-haiku-4-5-20251001";
    // Hard daily lid on Claude calls - a weird EDGAR day can't turn into a
    // token burst (the FD1 lesson). ~20p worst-case day on Haiku.
    public int MaxClassificationsPerDay { get; set; } = 40;
    // Which 8-K item codes route to classification. Null/empty = all seven
    // known codes (both directions).
    public string[]? RoutedItemCodes { get; set; }
}
