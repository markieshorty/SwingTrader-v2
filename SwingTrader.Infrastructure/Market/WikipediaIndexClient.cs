using HtmlAgilityPack;

namespace SwingTrader.Infrastructure.Market;

public class WikipediaIndexClient(HttpClient http) : IWikipediaIndexClient
{
    // ?action=raw would be lighter, but the rendered article HTML is what
    // has stable "wikitable" markup to select against.
    private const string Sp500Url = "https://en.wikipedia.org/wiki/List_of_S%26P_500_companies";
    private const string Nasdaq100Url = "https://en.wikipedia.org/wiki/Nasdaq-100";

    public Task<List<string>> GetSp500ConstituentsAsync(CancellationToken ct = default) =>
        FetchTickerColumnAsync(Sp500Url, ct);

    public Task<List<string>> GetNasdaq100ConstituentsAsync(CancellationToken ct = default) =>
        FetchTickerColumnAsync(Nasdaq100Url, ct);

    // The two pages use different header text ("Symbol" vs "Ticker") and
    // aren't guaranteed to be the first wikitable on the page (e.g. sector
    // weighting tables can appear first) - so this finds the constituents
    // table by its header rather than assuming a fixed table index.
    private async Task<List<string>> FetchTickerColumnAsync(string url, CancellationToken ct)
    {
        var html = await http.GetStringAsync(url, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var tables = doc.DocumentNode.SelectNodes("//table[contains(concat(' ', normalize-space(@class), ' '), ' wikitable ')]");
        if (tables is null) return [];

        foreach (var table in tables)
        {
            var headerCells = table.SelectNodes(".//tr[1]/th");
            if (headerCells is null) continue;

            var tickerColumnIndex = -1;
            for (var i = 0; i < headerCells.Count; i++)
            {
                var headerText = headerCells[i].InnerText.Trim();
                if (headerText.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
                    || headerText.Equals("Ticker", StringComparison.OrdinalIgnoreCase))
                {
                    tickerColumnIndex = i;
                    break;
                }
            }
            if (tickerColumnIndex < 0) continue;

            var rows = table.SelectNodes(".//tr[position()>1]");
            if (rows is null) continue;

            var symbols = new List<string>();
            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./td");
                if (cells is null || cells.Count <= tickerColumnIndex) continue;

                var symbol = HtmlEntity.DeEntitize(cells[tickerColumnIndex].InnerText).Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(symbol)) symbols.Add(symbol);
            }

            if (symbols.Count > 0) return symbols;
        }

        return [];
    }
}
