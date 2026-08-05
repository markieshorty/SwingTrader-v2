using System.IO.Compression;
using System.Text.Json;
using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.Storage;

// Serialization + merge logic for the blob-backed historic candle store
// (docs/blob-candles-plan), kept free of any Azure dependency so it unit-tests
// without a storage emulator. One blob per symbol: a gzipped JSON array of
// [date, open, high, low, close, volume] rows, date-sorted and deduped.
public static class CandleBlobCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static byte[] Encode(IReadOnlyList<HistoricalCandle> bars)
    {
        var rows = bars
            .OrderBy(b => b.Date)
            .Select(b => new object[] { b.Date.ToString("yyyy-MM-dd"), b.Open, b.High, b.Low, b.Close, b.Volume });
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, rows, Options);
        return ms.ToArray();
    }

    public static List<HistoricalCandle> Decode(string symbol, Stream gzipped)
    {
        using var gz = new GZipStream(gzipped, CompressionMode.Decompress);
        var rows = JsonSerializer.Deserialize<List<JsonElement[]>>(gz, Options) ?? [];
        return rows.Select(r => new HistoricalCandle
        {
            Symbol = symbol,
            Date = DateOnly.Parse(r[0].GetString()!),
            Open = r[1].GetDecimal(),
            High = r[2].GetDecimal(),
            Low = r[3].GetDecimal(),
            Close = r[4].GetDecimal(),
            Volume = r[5].GetDecimal(),
        }).ToList();
    }

    // Append-mostly merge: incoming bars win on date collisions (a re-sync of
    // an existing day replaces it), result stays date-sorted and deduped.
    public static List<HistoricalCandle> Merge(
        IReadOnlyList<HistoricalCandle> existing, IReadOnlyList<HistoricalCandle> incoming)
    {
        var byDate = existing.ToDictionary(b => b.Date);
        foreach (var bar in incoming)
            byDate[bar.Date] = bar;
        return byDate.Values.OrderBy(b => b.Date).ToList();
    }
}

// meta.json: the small index that answers every "shape of the dataset"
// question (latest/earliest dates, counts, max date, dataset version) without
// downloading a single bar blob. Rewritten at the end of each write batch -
// writers are already serialized by the single sync/backfill consumer.
public class CandleStoreMeta
{
    public int DatasetVersion { get; set; } = 1;
    public DateTime? LastDelistedBackfillAt { get; set; }
    public Dictionary<string, CandleSymbolMeta> Symbols { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // System.Text.Json rebuilds Symbols with the DEFAULT comparer on
    // deserialize - every read path must call this or symbol lookups turn
    // case-sensitive and "aapl" quietly misses "AAPL".
    public CandleStoreMeta Normalize()
    {
        Symbols = new Dictionary<string, CandleSymbolMeta>(Symbols, StringComparer.OrdinalIgnoreCase);
        return this;
    }
}

public class CandleSymbolMeta
{
    public DateOnly Min { get; set; }
    public DateOnly Max { get; set; }
    public int Count { get; set; }
}
