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
}

public sealed record EdgarFilingRef(
    string AccessionNumber, string FilingType, DateOnly FiledAt, string PrimaryDocument);

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

    public async Task<IReadOnlyList<EdgarFilingRef>> GetRecentFilingsAsync(
        string cik, IReadOnlyCollection<string> filingTypes, CancellationToken ct)
    {
        var json = await GetStringAsync($"{DataHost}/submissions/CIK{cik}.json", ct);
        var doc = JsonSerializer.Deserialize<SubmissionsResponse>(json, JsonOpts);
        var recent = doc?.Filings?.Recent;
        if (recent?.AccessionNumber is null) return [];

        var results = new List<EdgarFilingRef>();
        // The "recent" block is parallel arrays, newest first.
        for (var i = 0; i < recent.AccessionNumber.Count; i++)
        {
            var form = recent.Form.ElementAtOrDefault(i);
            if (form is null || !filingTypes.Contains(form)) continue;
            if (!DateOnly.TryParse(recent.FilingDate.ElementAtOrDefault(i), out var filed)) continue;
            var primary = recent.PrimaryDocument.ElementAtOrDefault(i);
            if (string.IsNullOrWhiteSpace(primary)) continue;
            results.Add(new EdgarFilingRef(recent.AccessionNumber[i], form, filed, primary));
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
        [property: JsonPropertyName("primaryDocument")] List<string> PrimaryDocument);
}
