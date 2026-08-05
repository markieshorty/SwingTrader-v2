using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Infrastructure.Storage;

public interface ICandleBlobMigrationService
{
    Task<CandleBlobMigrationResult> RunChunkAsync(CancellationToken ct = default);
}

public record CandleBlobMigrationResult(int Migrated, int Remaining, string Summary);

// One-off SQL -> blob copy for the historic candle store
// (docs/blob-candles-plan). Chunked and resumable exactly like the delisted
// backfill: each invocation migrates up to ChunkSize symbols (skipping any
// already in the blob meta), and the consumer re-enqueues a continuation
// message while Remaining > 0. Per-symbol SQL reads are targeted queries -
// Basic-tier safe. Runs while HistoricStore:UseBlob is still FALSE, so
// `source` resolves to the SQL repository; a completed run's summary carries
// the SQL-vs-blob bar-count comparison that gates the flag flip.
public class CandleBlobMigrationService(
    IHistoricalCandleRepository source,
    BlobHistoricalCandleRepository blobStore,
    ILogger<CandleBlobMigrationService> logger) : ICandleBlobMigrationService
{
    private const int ChunkSize = 150;

    public async Task<CandleBlobMigrationResult> RunChunkAsync(CancellationToken ct = default)
    {
        if (source is BlobHistoricalCandleRepository)
            return new CandleBlobMigrationResult(0, 0,
                "HistoricStore:UseBlob is already ON — the migration reads from SQL and must run before the flip.");

        var sqlSymbols = (await source.GetLatestDatesAsync(ct)).Keys.ToList();
        var meta = await blobStore.GetMetaAsync(ct);
        var pending = sqlSymbols
            .Where(s => !meta.Symbols.ContainsKey(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chunk = pending.Take(ChunkSize).ToList();
        var migratedBars = 0;
        foreach (var symbol in chunk)
        {
            ct.ThrowIfCancellationRequested();
            var bars = await source.GetForSymbolsAsync([symbol], DateOnly.MinValue, ct);
            if (bars.TryGetValue(symbol, out var list) && list.Count > 0)
            {
                await blobStore.AddRangeAsync(list, ct);
                migratedBars += list.Count;
            }
        }

        var remaining = pending.Count - chunk.Count;
        string summary;
        if (remaining > 0)
        {
            summary = $"Blob migration: {chunk.Count} symbol(s) copied this chunk ({migratedBars} bars), {remaining} to go.";
        }
        else
        {
            // Completion: seed the dataset version (fingerprints must not
            // shift at cutover) and report the verification numbers.
            await blobStore.SetDatasetVersionAsync(
                await source.GetDatasetVersionAsync(ct), DateTime.UtcNow, ct);
            var sqlCount = await source.CountAsync(ct);
            var blobCount = await blobStore.CountAsync(ct);
            summary = $"Blob migration COMPLETE: SQL {sqlCount} bars vs blob {blobCount} bars " +
                      $"({(sqlCount == blobCount ? "MATCH — safe to flip HistoricStore:UseBlob" : "MISMATCH — investigate before flipping")}).";
        }

        logger.LogInformation("{Summary}", summary);
        return new CandleBlobMigrationResult(chunk.Count, remaining, summary);
    }
}
