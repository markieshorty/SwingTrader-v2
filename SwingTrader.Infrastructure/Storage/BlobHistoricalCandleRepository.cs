using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.Storage;

// Blob-backed historic candle store (docs/blob-candles-plan): same
// IHistoricalCandleRepository contract as the SQL implementation, selected by
// HistoricStore:UseBlob. One gzipped blob per symbol + a small meta.json
// index; the backtester's whole-store load becomes a bounded-parallel blob
// download instead of a 5-DTU table scan.
public class BlobHistoricalCandleRepository(
    IConfiguration config,
    ILogger<BlobHistoricalCandleRepository> logger) : IHistoricalCandleRepository
{
    private const string ContainerName = "historic-candles";
    private const string MetaBlobName = "meta.json";
    private const int MaxParallelDownloads = 16;

    private BlobContainerClient? _container;

    private BlobContainerClient Container()
    {
        if (_container is not null) return _container;
        var conn = config["HistoricStore:BlobConnection"];
        if (string.IsNullOrWhiteSpace(conn)) conn = config["AzureWebJobsStorage"];
        if (string.IsNullOrWhiteSpace(conn))
            throw new InvalidOperationException(
                "Blob candle store needs HistoricStore:BlobConnection (or AzureWebJobsStorage) configured.");
        _container = new BlobServiceClient(conn).GetBlobContainerClient(ContainerName);
        _container.CreateIfNotExists();
        return _container;
    }

    private static string BarsBlobName(string symbol) => $"bars/{symbol.ToUpperInvariant()}.json.gz";

    // ── meta.json ────────────────────────────────────────────────────────────

    public async Task<CandleStoreMeta> GetMetaAsync(CancellationToken ct = default)
    {
        try
        {
            var download = await Container().GetBlobClient(MetaBlobName).DownloadContentAsync(ct);
            return (download.Value.Content.ToObjectFromJson<CandleStoreMeta>() ?? new CandleStoreMeta()).Normalize();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new CandleStoreMeta();
        }
    }

    private async Task SaveMetaAsync(CandleStoreMeta meta, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(meta);
        await Container().GetBlobClient(MetaBlobName).UploadAsync(new BinaryData(json), overwrite: true, ct);
    }

    // ── reads ────────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, DateOnly>> GetLatestDatesAsync(CancellationToken ct = default) =>
        (await GetMetaAsync(ct)).Symbols.ToDictionary(kv => kv.Key, kv => kv.Value.Max, StringComparer.OrdinalIgnoreCase);

    public async Task<Dictionary<string, DateOnly>> GetEarliestDatesAsync(CancellationToken ct = default) =>
        (await GetMetaAsync(ct)).Symbols.ToDictionary(kv => kv.Key, kv => kv.Value.Min, StringComparer.OrdinalIgnoreCase);

    public async Task<int> CountAsync(CancellationToken ct = default) =>
        (await GetMetaAsync(ct)).Symbols.Values.Sum(s => s.Count);

    public async Task<DateOnly?> GetMaxDateAsync(CancellationToken ct = default)
    {
        var meta = await GetMetaAsync(ct);
        return meta.Symbols.Count == 0 ? null : meta.Symbols.Values.Max(s => s.Max);
    }

    public async Task<Dictionary<string, List<HistoricalCandle>>> GetAllBySymbolAsync(CancellationToken ct = default)
    {
        var meta = await GetMetaAsync(ct);
        return await DownloadSymbolsAsync(meta.Symbols.Keys, ct);
    }

    public async Task<Dictionary<string, List<HistoricalCandle>>> GetForSymbolsAsync(
        IReadOnlyCollection<string> symbols, DateOnly from, CancellationToken ct = default)
    {
        var meta = await GetMetaAsync(ct);
        var wanted = symbols.Where(s => meta.Symbols.ContainsKey(s)).ToList();
        var all = await DownloadSymbolsAsync(wanted, ct);
        return all.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(b => b.Date >= from).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, List<HistoricalCandle>>> DownloadSymbolsAsync(
        IEnumerable<string> symbols, CancellationToken ct)
    {
        var result = new Dictionary<string, List<HistoricalCandle>>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(MaxParallelDownloads);
        var tasks = symbols.Select(async symbol =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var download = await Container().GetBlobClient(BarsBlobName(symbol)).DownloadContentAsync(ct);
                var bars = CandleBlobCodec.Decode(symbol, download.Value.Content.ToStream());
                lock (result) result[symbol] = bars;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Meta listed a symbol whose blob is gone - skip rather than
                // fail a whole backtest; the next write batch heals the meta.
                logger.LogWarning("Candle blob missing for {Symbol} — skipped", symbol);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return result;
    }

    // ── writes ───────────────────────────────────────────────────────────────

    public async Task AddRangeAsync(IEnumerable<HistoricalCandle> candles, CancellationToken ct = default)
    {
        var bySymbol = candles.GroupBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase).ToList();
        if (bySymbol.Count == 0) return;

        var meta = await GetMetaAsync(ct);
        foreach (var group in bySymbol)
        {
            ct.ThrowIfCancellationRequested();
            var symbol = group.Key.ToUpperInvariant();
            var incoming = group.ToList();

            List<HistoricalCandle> merged;
            var blob = Container().GetBlobClient(BarsBlobName(symbol));
            try
            {
                var existing = CandleBlobCodec.Decode(symbol, (await blob.DownloadContentAsync(ct)).Value.Content.ToStream());
                merged = CandleBlobCodec.Merge(existing, incoming);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                merged = CandleBlobCodec.Merge([], incoming);
            }

            await blob.UploadAsync(new BinaryData(CandleBlobCodec.Encode(merged)), overwrite: true, ct);
            meta.Symbols[symbol] = new CandleSymbolMeta
            {
                Min = merged[0].Date,
                Max = merged[^1].Date,
                Count = merged.Count,
            };
        }
        await SaveMetaAsync(meta, ct);
    }

    // ── dataset info ─────────────────────────────────────────────────────────

    // Blob mode has no meaningful size cap - reporting 0 deliberately disarms
    // the delisted backfill's 1600MB Basic-tier gate (docs/blob-candles-plan).
    public Task<decimal> GetDatabaseSizeMbAsync(CancellationToken ct = default) => Task.FromResult(0m);

    public async Task<int> GetDatasetVersionAsync(CancellationToken ct = default) =>
        (await GetMetaAsync(ct)).DatasetVersion;

    public async Task BumpDatasetVersionAsync(CancellationToken ct = default)
    {
        var meta = await GetMetaAsync(ct);
        meta.DatasetVersion++;
        meta.LastDelistedBackfillAt = DateTime.UtcNow;
        await SaveMetaAsync(meta, ct);
    }

    // Used by the migration to seed the blob store's version from SQL so
    // ConfigFingerprints don't shift at cutover.
    public async Task SetDatasetVersionAsync(int version, DateTime? lastBackfillAt, CancellationToken ct = default)
    {
        var meta = await GetMetaAsync(ct);
        meta.DatasetVersion = version;
        meta.LastDelistedBackfillAt = lastBackfillAt;
        await SaveMetaAsync(meta, ct);
    }
}
