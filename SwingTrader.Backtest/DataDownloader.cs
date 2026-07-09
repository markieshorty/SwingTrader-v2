using System.Globalization;
using System.Text;
using Refit;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;

namespace SwingTrader.Backtest;

// One-time bulk download of daily adjusted OHLCV for the whole screening
// universe (S&P 1500 + Nasdaq-100, via the same Wikipedia scrape production
// uses) plus SPY, from Tiingo. One CSV per symbol under the output directory;
// existing files are skipped so the command is resumable. Requires a Tiingo
// Power (paid) key - the free tier's 500-unique-symbols/month cap can't cover
// the universe.
public static class DataDownloader
{
    private const string TiingoBaseUrl = "https://api.tiingo.com";

    public static async Task<int> RunAsync(string apiKey, string outDir, int years, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);

        var universe = await FetchUniverseAsync(ct);
        // SPY anchors regime/relative-strength calcs. (VIX isn't available on
        // Tiingo's equity endpoint - the replay derives volatility from SPY.)
        var symbols = universe.Prepend("SPY").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine($"Universe: {symbols.Count} symbols. Output: {Path.GetFullPath(outDir)}");

        var http = new HttpClient { BaseAddress = new Uri(TiingoBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
        var tiingo = RestService.For<ITiingoClient>(http);

        var startDate = DateTime.UtcNow.AddYears(-years).ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        int done = 0, skipped = 0, failed = 0, empty = 0;
        var failures = new List<string>();

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) break;

            var path = Path.Combine(outDir, $"{symbol.ToUpperInvariant()}.csv");
            if (File.Exists(path))
            {
                skipped++;
                continue;
            }

            try
            {
                var prices = await tiingo.GetDailyPricesAsync(symbol, startDate, endDate);
                if (prices is not { Count: > 0 })
                {
                    empty++;
                    Console.WriteLine($"  {symbol}: no data (delisted/renamed?) — skipping");
                }
                else
                {
                    var sb = new StringBuilder("Date,Open,High,Low,Close,Volume,AdjOpen,AdjHigh,AdjLow,AdjClose,AdjVolume\n");
                    foreach (var p in prices.OrderBy(p => p.Date))
                    {
                        sb.Append(p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                          .Append(p.Open).Append(',').Append(p.High).Append(',').Append(p.Low).Append(',')
                          .Append(p.Close).Append(',').Append(p.Volume).Append(',')
                          .Append(p.AdjOpen).Append(',').Append(p.AdjHigh).Append(',').Append(p.AdjLow).Append(',')
                          .Append(p.AdjClose).Append(',').Append(p.AdjVolume).Append('\n');
                    }
                    await File.WriteAllTextAsync(path, sb.ToString(), ct);
                    done++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add($"{symbol}: {ex.Message}");
                Console.WriteLine($"  {symbol}: FAILED — {ex.Message}");
            }

            var progress = done + skipped + failed + empty;
            if (progress % 50 == 0)
                Console.WriteLine($"[{progress}/{symbols.Count}] downloaded={done} cached={skipped} empty={empty} failed={failed}");

            // Pace well inside Tiingo Power's hourly allowance.
            await Task.Delay(400, ct);
        }

        if (failures.Count > 0)
            await File.WriteAllLinesAsync(Path.Combine(outDir, "_failed.txt"), failures, ct);

        Console.WriteLine($"\nDone. downloaded={done} cached={skipped} empty={empty} failed={failed}");
        Console.WriteLine(failed > 0 ? "Failures listed in _failed.txt — re-run to retry (existing files are skipped)." : "All symbols fetched.");
        return failed > 0 ? 1 : 0;
    }

    private static async Task<List<string>> FetchUniverseAsync(CancellationToken ct)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwingTraderBot/1.0 (personal swing-trading app; contact via GitHub repo)");
        var wikipedia = new WikipediaIndexClient(http);

        var all = new List<UniverseSymbol>();
        foreach (var fetch in new Func<CancellationToken, Task<List<UniverseSymbol>>>[]
                 { wikipedia.GetSp500ConstituentsAsync, wikipedia.GetSp400ConstituentsAsync, wikipedia.GetSp600ConstituentsAsync, wikipedia.GetNasdaq100ConstituentsAsync })
        {
            try { all.AddRange(await fetch(ct)); }
            catch (Exception ex) { Console.WriteLine($"Universe fetch failed for one index: {ex.Message}"); }
        }

        return all.Select(u => u.Symbol.ToUpperInvariant())
            .Where(s => s.All(char.IsLetter) && s.Length is >= 1 and <= 5)
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }
}
