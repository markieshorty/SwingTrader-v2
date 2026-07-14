using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Edgar;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.Filings;

public record FilingSyncResult(bool Configured, int FilingsStored, int DeltasScored, int Unchanged, int Failed, string Summary);

public interface IFilingSyncService
{
    Task<FilingSyncResult> SyncAsync(CancellationToken ct = default);
}

// Platform-level daily job (docs/filing-delta-plan Phase FD1): for the union
// of every account's watchlist symbols, pull new 10-K/10-Q filings from
// EDGAR, extract + hash the comparable sections, and score the diff with
// Claude ONLY when a hash actually changed - the copy-paste quarter (the
// common case) is detected free of tokens, and the change is the signal.
//
// 8-K velocity is deliberately deferred to Phase FD3: episodic filings have
// no per-type "previous document" to diff against, so they need their own
// baseline design rather than riding this one dishonestly.
public class FilingSyncService(
    IFilingRepository filings,
    IWatchlistRepository watchlists,
    IEdgarClient edgar,
    IUserHttpClientFactory clientFactory,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<FilingDeltaConfig> config,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<FilingSyncService> logger) : IFilingSyncService
{
    private static readonly string[] FilingTypes = ["10-K", "10-Q"];
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<FilingSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var cfg = config.Value;
        if (!cfg.Enabled)
            return new FilingSyncResult(false, 0, 0, 0, 0, "FilingSync disabled (FilingDelta:Enabled=false).");

        var symbols = await watchlists.GetActiveSymbolsAcrossAccountsAsync(ct);
        if (symbols.Count == 0)
            return new FilingSyncResult(true, 0, 0, 0, 0, "No watchlist symbols to sync filings for.");

        IReadOnlyDictionary<string, string> cikMap;
        try
        {
            cikMap = await edgar.GetCikMapAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EDGAR CIK map unavailable — filing sync skipped this run");
            return new FilingSyncResult(true, 0, 0, 0, 1, $"EDGAR CIK map unavailable: {ex.Message}");
        }

        IClaudeClient? claude = null; // created lazily - most runs never need it

        int stored = 0, scored = 0, unchanged = 0, failed = 0;
        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) break;
            if (!cikMap.TryGetValue(symbol, out var cik))
            {
                logger.LogDebug("{Symbol}: no EDGAR CIK (non-SEC filer or ETF) — skipping", symbol);
                continue;
            }

            try
            {
                var recent = await edgar.GetRecentFilingsAsync(cik, FilingTypes, ct);

                foreach (var type in FilingTypes)
                {
                    // Newest first from EDGAR; take up to 2 so a symbol's very
                    // first sync backfills the previous filing and produces a
                    // day-one delta (spec's backfill requirement). Processed
                    // oldest-first so each becomes the next one's baseline.
                    var candidates = recent
                        .Where(f => f.FilingType == type)
                        .Take(2)
                        .Reverse()
                        .ToList();

                    foreach (var filingRef in candidates)
                    {
                        if (await filings.ExistsAsync(filingRef.AccessionNumber, ct)) continue;

                        var previous = await filings.GetLatestAsync(symbol, type, ct);
                        var filing = await FetchAndExtractAsync(symbol, cik, filingRef, cfg, ct);
                        filing = await filings.AddAsync(filing, ct);
                        stored++;

                        if (previous is null || filing.ParseFailed || previous.ParseFailed)
                            continue; // nothing comparable - no delta row, degraded-null downstream

                        var (changedSections, diffText) = BuildDiff(previous, filing, cfg);
                        if (changedSections.Count == 0)
                        {
                            await filings.AddDeltaAsync(new FilingDelta
                            {
                                FilingId = filing.Id, Symbol = symbol, FiledAt = filing.FiledAt,
                                Direction = 0m, Materiality = 0m, Delta = 0m,
                            }, ct);
                            unchanged++;
                            continue;
                        }

                        claude ??= await clientFactory.CreateClaudeAsync<IClaudeClient>(SwingTraderDbContext.SystemAccountId, ct);
                        var delta = await ScoreDiffAsync(claude, symbol, type, changedSections, diffText, ct);
                        delta.FilingId = filing.Id;
                        delta.Symbol = symbol;
                        delta.FiledAt = filing.FiledAt;
                        await filings.AddDeltaAsync(delta, ct);
                        scored++;
                        logger.LogInformation(
                            "Filing delta scored: {Symbol} {Type} {Filed} — delta {Delta:+0.00;-0.00} ({Summary})",
                            symbol, type, filing.FiledAt, delta.Delta, delta.Summary);
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Filing sync failed for {Symbol} — continuing", symbol);
            }
        }

        var summary = $"FilingSync: {stored} filings stored, {scored} deltas scored, {unchanged} unchanged, {failed} failed ({symbols.Count} symbols).";
        logger.LogInformation("{Summary}", summary);
        return new FilingSyncResult(true, stored, scored, unchanged, failed, summary);
    }

    private async Task<Filing> FetchAndExtractAsync(
        string symbol, string cik, EdgarFilingRef filingRef, FilingDeltaConfig cfg, CancellationToken ct)
    {
        var filing = new Filing
        {
            Symbol = symbol,
            Cik = cik,
            AccessionNumber = filingRef.AccessionNumber,
            FilingType = filingRef.FilingType,
            FiledAt = filingRef.FiledAt,
            PrimaryDocument = filingRef.PrimaryDocument,
        };

        try
        {
            var html = await edgar.GetDocumentAsync(cik, filingRef.AccessionNumber, filingRef.PrimaryDocument, ct);
            var text = FilingTextExtractor.HtmlToText(html);
            var sections = FilingTextExtractor.ExtractSections(text, filingRef.FilingType);

            if (sections.RiskFactors is not null)
            {
                filing.RiskFactorsText = Truncate(sections.RiskFactors, cfg.MaxSectionChars);
                filing.RiskFactorsHash = FilingTextExtractor.Hash(filing.RiskFactorsText);
            }
            if (sections.Mda is not null)
            {
                filing.MdaText = Truncate(sections.Mda, cfg.MaxSectionChars);
                filing.MdaHash = FilingTextExtractor.Hash(filing.MdaText);
            }
            // Both sections missing = the heuristics found nothing usable.
            filing.ParseFailed = filing.RiskFactorsHash is null && filing.MdaHash is null;
        }
        catch (OperationCanceledException)
        {
            // A shutdown mid-fetch must NOT be stored as ParseFailed: the
            // accession-exists check would then never refetch this filing,
            // making a transient cancellation a permanent no-score.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Filing fetch/extract failed for {Symbol} {Accession}", symbol, filingRef.AccessionNumber);
            filing.ParseFailed = true;
        }

        return filing;
    }

    private static (List<string> ChangedSections, string DiffText) BuildDiff(
        Filing previous, Filing current, FilingDeltaConfig cfg)
    {
        var changed = new List<string>();
        var parts = new List<string>();

        void CompareSection(string name, string? prevHash, string? prevText, string? currHash, string? currText)
        {
            // Identical hashes are the free no-op; a section missing on either
            // side has nothing comparable (extraction gap, not a language change).
            if (prevHash is null || currHash is null || prevHash == currHash) return;

            // Hash changed but the paragraph diff finds nothing substantial
            // (only sub-80-char edits) -> still unchanged for scoring
            // purposes; don't send Claude an empty diff.
            var diff = FilingTextExtractor.DiffParagraphs(prevText ?? "", currText ?? "");
            if (diff.Added.Count == 0 && diff.Removed.Count == 0) return;

            changed.Add(name);
            // Full-fidelity paragraphs (no clipping - Mark, 14 Jul 2026, after
            // the premium model moved from Opus to Sonnet). Only the COUNT is
            // capped, and when that binds the LONGEST paragraphs are kept
            // (substance over boilerplate reshuffles); the full counts in the
            // header still tell Claude how much was left unshown.
            var added = diff.Added.OrderByDescending(p => p.Length)
                .Take(cfg.MaxDiffParagraphs).ToList();
            var removed = diff.Removed.OrderByDescending(p => p.Length)
                .Take(cfg.MaxDiffParagraphs).ToList();
            parts.Add(
                $"## Section: {name}\n" +
                $"### Paragraphs ADDED in the new filing ({diff.Added.Count} total, showing {added.Count}):\n" +
                string.Join("\n---\n", added) +
                $"\n### Paragraphs REMOVED since the previous filing ({diff.Removed.Count} total, showing {removed.Count}):\n" +
                string.Join("\n---\n", removed));
        }

        CompareSection("Risk Factors", previous.RiskFactorsHash, previous.RiskFactorsText, current.RiskFactorsHash, current.RiskFactorsText);
        CompareSection("MD&A", previous.MdaHash, previous.MdaText, current.MdaHash, current.MdaText);

        return (changed, string.Join("\n\n", parts));
    }

    private async Task<FilingDelta> ScoreDiffAsync(
        IClaudeClient claude, string symbol, string filingType, List<string> changedSections, string diffText, CancellationToken ct)
    {
        var systemPrompt =
            "You are a forensic financial-filings analyst. Companies write SEC filings by copy-paste; " +
            "when the language changes, it is deliberate. Respond only with valid JSON.";

        var userPrompt =
            $"The following shows what changed in {symbol}'s latest {filingType} " +
            $"({string.Join(" and ", changedSections)}) compared to their previous {filingType}.\n\n" +
            $"{diffText}\n\n" +
            "Assess what management is signalling by these changes. Respond with this exact JSON structure:\n" +
            "{\n" +
            "  \"direction\": <float -1.0 to 1.0: negative = new/expanded risks, hedging added, disclosures that presage deterioration; positive = risks removed, language de-hedged, disclosures that presage improvement>,\n" +
            "  \"materiality\": <float 0.0 to 1.0: 0 = boilerplate/legal reshuffle with no information; 1 = a change every serious investor should know about>,\n" +
            "  \"categories\": [\"<labels such as litigation, liquidity, customer-concentration, guidance-language, going-concern, competition, regulation>\"],\n" +
            "  \"summary\": \"<2-3 plain-English sentences: what changed and why it matters>\"\n" +
            "}\n\n" +
            "Rules: routine updates (dates rolled forward, standard legal edits, reordered but equivalent text) are " +
            "materiality 0. Judge only what is IN the diff - do not speculate beyond it.";

        await claudeRateLimiter.WaitAsync(ct);
        var response = await claude.SendMessageAsync(new ClaudeRequest(
            claudeConfig.Value.PremiumModel, claudeConfig.Value.MaxTokens, systemPrompt,
            [new ClaudeMessage("user", userPrompt)]));
        var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        return ParseDeltaResponse(raw, claudeConfig.Value.PremiumModel);
    }

    // Internal static so the clamping/parse rules are directly testable.
    internal static FilingDelta ParseDeltaResponse(string raw, string model)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }

        var parsed = JsonSerializer.Deserialize<DeltaResponse>(text, JsonOpts)
            ?? throw new JsonException("null filing-delta result");

        var direction = Math.Clamp((decimal)parsed.Direction, -1m, 1m);
        var materiality = Math.Clamp((decimal)parsed.Materiality, 0m, 1m);
        return new FilingDelta
        {
            Direction = Math.Round(direction, 4),
            Materiality = Math.Round(materiality, 4),
            Delta = Math.Round(direction * materiality, 4),
            Categories = parsed.Categories is { Count: > 0 } c ? string.Join(",", c.Take(10)) : null,
            Summary = string.IsNullOrWhiteSpace(parsed.Summary) ? null : parsed.Summary.Trim(),
            Model = model,
        };
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];

    private sealed record DeltaResponse(double Direction, double Materiality, List<string>? Categories, string? Summary);
}
