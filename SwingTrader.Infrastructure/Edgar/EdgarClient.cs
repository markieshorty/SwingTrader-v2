using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Infrastructure.Edgar;

public interface IEdgarClient
{
    // Symbol -> zero-padded 10-digit CIK, from EDGAR's company_tickers.json.
    Task<IReadOnlyDictionary<string, string>> GetCikMapAsync(CancellationToken ct);

    // A company's recent filings, newest first, filtered to the given types.
    Task<IReadOnlyList<EdgarFilingRef>> GetRecentFilingsAsync(
        string cik, IReadOnlyCollection<string> filingTypes, CancellationToken ct);

    // The primary document (HTML) of one filing.
    Task<string> GetDocumentAsync(string cik, string accessionNumber, string primaryDocument, CancellationToken ct);

    // Every 8-K filed on the given date, market-wide, via EDGAR full-text
    // search (docs/filing-events-plan P1) - includes item codes and tickers,
    // so routing needs no further requests.
    Task<IReadOnlyList<EdgarEightK>> SearchEightKsAsync(DateOnly date, CancellationToken ct);

    // Public float in USD from the company's latest cover-page XBRL, or null
    // when it has never reported one (foreign private issuers, fresh
    // listings). This is the SEC's own basis for "smaller reporting company"
    // (< $250M float), which makes it a far better size test than guessing
    // from an index membership list.
    Task<decimal?> GetPublicFloatAsync(string cik, CancellationToken ct);
}

// One market-wide 8-K search hit. Ticker may be empty (funds, co-filers).
public sealed record EdgarEightK(
    string Cik, string Ticker, string CompanyName, string AccessionNumber,
    string PrimaryDocument, DateOnly FiledAt, IReadOnlyList<string> Items,
    // SIC industry code, straight from the search hit - no extra request.
    // 6770 ("Blank Checks") is how SPAC shells identify themselves.
    string Sic = "");

// Items: the 8-K item codes for this filing (comma-separated, e.g.
// "3.01,9.01"), straight from the submissions JSON's parallel `items` array.
// Empty for 10-K/10-Q. This is what makes rules-based distress detection
// (FD3) free - the codes arrive in a fetch we already make.
public sealed record EdgarFilingRef(
    string AccessionNumber, string FilingType, DateOnly FiledAt, string PrimaryDocument, string Items = "");

// Thin EDGAR HTTP wrapper. No API key; the SEC requires a declared User-Agent
// and caps fair use at 10 req/s - every call is paced by EdgarDelayMs and the
// caller (FilingSync) is a once-daily platform job, so we sit far below it.
// Two hosts are involved (www.sec.gov for files/documents, data.sec.gov for
// the submissions API), so this wraps a raw HttpClient rather than Refit.
public class EdgarClient(
    HttpClient http,
    IOptions<FilingDeltaConfig> config,
    ILogger<EdgarClient> logger) : IEdgarClient
{
    private const string DataHost = "https://data.sec.gov";
    private const string WwwHost = "https://www.sec.gov";
    private const string SearchHost = "https://efts.sec.gov";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, string>> GetCikMapAsync(CancellationToken ct)
    {
        var json = await GetStringAsync($"{WwwHost}/files/company_tickers.json", ct);
        // Shape: { "0": {"cik_str":320193,"ticker":"AAPL","title":"Apple Inc."}, ... }
        var entries = JsonSerializer.Deserialize<Dictionary<string, CompanyTickerEntry>>(json, JsonOpts) ?? [];
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Values)
            map.TryAdd(e.Ticker, e.CikStr.ToString("D10"));
        logger.LogInformation("EDGAR CIK map loaded: {Count} tickers", map.Count);
        return map;
    }

    public async Task<IReadOnlyList<EdgarEightK>> SearchEightKsAsync(DateOnly date, CancellationToken ct)
    {
        // EDGAR full-text search API. Paged 100/hit-page; a heavy filing day
        // runs ~400-800 8-Ks so a 10-page cap covers it with headroom.
        var results = new List<EdgarEightK>();
        var d = date.ToString("yyyy-MM-dd");
        // 2,000 rather than 1,000: a busy day runs past 1,600 filings (6 Aug
        // 2026: 1,628), and because hits arrive in EDGAR's order a low cap
        // silently drops the late filers instead of a random subset. The loop
        // still exits on the first short page, so a quiet day costs no extra
        // requests. Verified 7 Aug 2026 that EDGAR serves from=1500+ happily.
        for (var from = 0; from < 2000; from += 100)
        {
            string json;
            try
            {
                json = await GetStringAsync(
                    $"{SearchHost}/LATEST/search-index?q=%22%22&forms=8-K&dateRange=custom&startdt={d}&enddt={d}&from={from}", ct);
            }
            catch (Exception ex)
            {
                // SEC throttles hard on rapid paging. Keep what we have -
                // a partial day of filings is useful, a thrown scan is not.
                logger.LogWarning(ex, "EDGAR 8-K search page {From} failed - continuing with {Count} filings", from, results.Count);
                break;
            }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hits", out var outer)
                || !outer.TryGetProperty("hits", out var hits)) break;

            var page = 0;
            foreach (var hit in hits.EnumerateArray())
            {
                page++;
                try
                {
                    var src = hit.GetProperty("_source");
                    // _id: "0001234567-26-000123:document.htm"
                    var id = hit.GetProperty("_id").GetString() ?? "";
                    var parts = id.Split(':');
                    if (parts.Length < 2) continue;
                    var accession = parts[0];
                    var primaryDoc = parts[1];

                    var cik = src.TryGetProperty("ciks", out var ciks) && ciks.GetArrayLength() > 0
                        ? (ciks[0].GetString() ?? "") : "";
                    // display_names: ["Acme Corp  (ACME)  (CIK 0001234567)"]
                    var display = src.TryGetProperty("display_names", out var names) && names.GetArrayLength() > 0
                        ? (names[0].GetString() ?? "") : "";
                    var (company, ticker) = ParseDisplayName(display);

                    var items = new List<string>();
                    if (src.TryGetProperty("items", out var itemsEl))
                        foreach (var it in itemsEl.EnumerateArray())
                            if (it.GetString() is { Length: > 0 } code) items.Add(code.Trim());

                    var sic = src.TryGetProperty("sics", out var sics) && sics.GetArrayLength() > 0
                        ? (sics[0].GetString() ?? "") : "";

                    var filed = src.TryGetProperty("file_date", out var fd)
                        && DateOnly.TryParse(fd.GetString(), out var f) ? f : date;

                    if (cik.Length > 0 && accession.Length > 0)
                        results.Add(new EdgarEightK(cik.PadLeft(10, '0'), ticker, company, accession, primaryDoc, filed, items, sic));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Unparseable EDGAR search hit — skipped");
                }
            }
            if (page < 100) break; // last page
        }
        logger.LogInformation("EDGAR 8-K search {Date}: {Count} filings", d, results.Count);
        return results;
    }

    public async Task<decimal?> GetPublicFloatAsync(string cik, CancellationToken ct)
    {
        var padded = cik.PadLeft(10, '0');
        try
        {
            var json = await GetStringAsync(
                $"{DataHost}/api/xbrl/companyconcept/CIK{padded}/dei/EntityPublicFloat.json", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("units", out var units)) return null;

            // Float is a cover-page fact restated each year; take the most
            // recent reported period rather than assuming document order.
            string? bestEnd = null;
            decimal? bestVal = null;
            foreach (var unit in units.EnumerateObject())
                foreach (var row in unit.Value.EnumerateArray())
                {
                    if (!row.TryGetProperty("end", out var endEl)) continue;
                    var end = endEl.GetString();
                    if (end is null || (bestEnd is not null && string.CompareOrdinal(end, bestEnd) <= 0)) continue;
                    if (!row.TryGetProperty("val", out var valEl)) continue;
                    if (!valEl.TryGetDecimal(out var val)) continue;
                    bestEnd = end;
                    bestVal = val;
                }
            return bestVal;
        }
        catch (HttpRequestException ex)
        {
            // A company that has never reported a float 404s here. That is a
            // normal answer, not a failure - the caller decides what to do
            // with "unknown".
            logger.LogDebug(ex, "EDGAR public float unavailable for CIK {Cik}", padded);
            return null;
        }
    }

    // "Acme Corp  (ACME)  (CIK 0001234567)" -> ("Acme Corp", "ACME").
    // Internal static so the parse is testable.
    internal static (string Company, string Ticker) ParseDisplayName(string display)
    {
        if (string.IsNullOrWhiteSpace(display)) return ("", "");
        var cikIdx = display.IndexOf("(CIK", StringComparison.OrdinalIgnoreCase);
        var head = (cikIdx >= 0 ? display[..cikIdx] : display).Trim();
        var open = head.LastIndexOf('(');
        var close = head.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var ticker = head[(open + 1)..close].Trim();
            var company = head[..open].Trim();
            // Multi-ticker entries ("ABC, ABC-WS") - take the plain first.
            ticker = ticker.Split(',')[0].Trim();
            return (company, ticker.All(c => char.IsAsciiLetterUpper(c) || c == '.' || c == '-') ? ticker : "");
        }
        return (head, "");
    }

    public async Task<IReadOnlyList<EdgarFilingRef>> GetRecentFilingsAsync(
        string cik, IReadOnlyCollection<string> filingTypes, CancellationToken ct)
    {
        var json = await GetStringAsync($"{DataHost}/submissions/CIK{cik}.json", ct);
        var doc = JsonSerializer.Deserialize<SubmissionsResponse>(json, JsonOpts);
        var recent = doc?.Filings?.Recent;
        // Parallel arrays: any one missing means the payload shape changed -
        // treat as "no filings" rather than NRE-ing the whole sync.
        if (recent?.AccessionNumber is null || recent.Form is null
            || recent.FilingDate is null || recent.PrimaryDocument is null)
            return [];

        var results = new List<EdgarFilingRef>();
        // The "recent" block is parallel arrays, newest first.
        for (var i = 0; i < recent.AccessionNumber.Count; i++)
        {
            var form = recent.Form.ElementAtOrDefault(i);
            if (form is null || !filingTypes.Contains(form)) continue;
            if (!DateOnly.TryParse(recent.FilingDate.ElementAtOrDefault(i), out var filed)) continue;
            var primary = recent.PrimaryDocument.ElementAtOrDefault(i);
            if (string.IsNullOrWhiteSpace(primary)) continue;
            // items is optional in the payload (and empty for 10-K/10-Q).
            var items = recent.Items?.ElementAtOrDefault(i) ?? "";
            results.Add(new EdgarFilingRef(recent.AccessionNumber[i], form, filed, primary, items));
        }
        return results;
    }

    public Task<string> GetDocumentAsync(string cik, string accessionNumber, string primaryDocument, CancellationToken ct)
    {
        // Archive paths use the unpadded CIK and the accession number without dashes.
        var cikTrimmed = cik.TrimStart('0');
        var accession = accessionNumber.Replace("-", "");
        return GetStringAsync($"{WwwHost}/Archives/edgar/data/{cikTrimmed}/{accession}/{primaryDocument}", ct);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        await Task.Delay(config.Value.EdgarDelayMs, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(config.Value.EdgarUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private sealed record CompanyTickerEntry(
        [property: JsonPropertyName("cik_str")] long CikStr,
        [property: JsonPropertyName("ticker")] string Ticker);

    private sealed record SubmissionsResponse([property: JsonPropertyName("filings")] SubmissionsFilings? Filings);
    private sealed record SubmissionsFilings([property: JsonPropertyName("recent")] SubmissionsRecent? Recent);

    private sealed record SubmissionsRecent(
        [property: JsonPropertyName("accessionNumber")] List<string> AccessionNumber,
        [property: JsonPropertyName("form")] List<string> Form,
        [property: JsonPropertyName("filingDate")] List<string> FilingDate,
        [property: JsonPropertyName("primaryDocument")] List<string> PrimaryDocument,
        [property: JsonPropertyName("items")] List<string>? Items = null);
}
