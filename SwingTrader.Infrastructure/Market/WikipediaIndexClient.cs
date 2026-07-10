using HtmlAgilityPack;

namespace SwingTrader.Infrastructure.Market;

public class WikipediaIndexClient(HttpClient http) : IWikipediaIndexClient
{
    // ?action=raw would be lighter, but the rendered article HTML is what
    // has stable "wikitable" markup to select against.
    private const string Sp500Url = "https://en.wikipedia.org/wiki/List_of_S%26P_500_companies";
    private const string Nasdaq100Url = "https://en.wikipedia.org/wiki/Nasdaq-100";
    private const string Sp400Url = "https://en.wikipedia.org/wiki/List_of_S%26P_400_companies";
    private const string Sp600Url = "https://en.wikipedia.org/wiki/List_of_S%26P_600_companies";

    public Task<List<UniverseSymbol>> GetSp500ConstituentsAsync(CancellationToken ct = default) =>
        FetchConstituentsAsync(Sp500Url, ct);

    public Task<List<UniverseSymbol>> GetNasdaq100ConstituentsAsync(CancellationToken ct = default) =>
        FetchConstituentsAsync(Nasdaq100Url, ct);

    public Task<List<UniverseSymbol>> GetSp400ConstituentsAsync(CancellationToken ct = default) =>
        FetchConstituentsAsync(Sp400Url, ct);

    public Task<List<UniverseSymbol>> GetSp600ConstituentsAsync(CancellationToken ct = default) =>
        FetchConstituentsAsync(Sp600Url, ct);

    // The pages use different header text (ticker: "Symbol" vs "Ticker"; name:
    // "Security" vs "Company") and aren't guaranteed to be the first wikitable
    // on the page (e.g. sector-weighting tables can appear first) - so this
    // finds the constituents table by its header rather than a fixed index, and
    // captures the company-name column alongside the ticker.
    private async Task<List<UniverseSymbol>> FetchConstituentsAsync(string url, CancellationToken ct)
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

            int tickerColumnIndex = -1, nameColumnIndex = -1, sectorColumnIndex = -1;
            for (var i = 0; i < headerCells.Count; i++)
            {
                var headerText = headerCells[i].InnerText.Trim();
                if (tickerColumnIndex < 0
                    && (headerText.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
                        || headerText.Equals("Ticker", StringComparison.OrdinalIgnoreCase)))
                    tickerColumnIndex = i;
                else if (nameColumnIndex < 0
                    && (headerText.Equals("Security", StringComparison.OrdinalIgnoreCase)
                        || headerText.Equals("Company", StringComparison.OrdinalIgnoreCase)))
                    nameColumnIndex = i;
                else if (sectorColumnIndex < 0
                    && headerText.Equals("GICS Sector", StringComparison.OrdinalIgnoreCase))
                    sectorColumnIndex = i;
            }
            if (tickerColumnIndex < 0) continue;

            var rows = table.SelectNodes(".//tr[position()>1]");
            if (rows is null) continue;

            var constituents = new List<UniverseSymbol>();
            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./td");
                if (cells is null || cells.Count <= tickerColumnIndex) continue;

                var symbol = HtmlEntity.DeEntitize(cells[tickerColumnIndex].InnerText).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(symbol)) continue;

                var name = nameColumnIndex >= 0 && cells.Count > nameColumnIndex
                    ? HtmlEntity.DeEntitize(cells[nameColumnIndex].InnerText).Trim()
                    : string.Empty;

                // GICS sector feeds SectorEtfMap.Resolve; null when the table
                // has no sector column (Nasdaq-100) or the cell is empty.
                var sector = sectorColumnIndex >= 0 && cells.Count > sectorColumnIndex
                    ? HtmlEntity.DeEntitize(cells[sectorColumnIndex].InnerText).Trim()
                    : null;

                constituents.Add(new UniverseSymbol(symbol, name, string.IsNullOrEmpty(sector) ? null : sector));
            }

            if (constituents.Count > 0) return constituents;
        }

        return [];
    }
}
