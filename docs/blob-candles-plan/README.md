# Historic Candles on Blob Storage

Status: **spec agreed 4 Aug 2026 (late evening), built same night.**

## Why

The historic candle store (~3.6M rows, ~10y × ~1,500 symbols + delisted
universe) is the wrong shape for Azure SQL and the wrong cost:

- **Access pattern is a file, not a table.** The backtester does ONE bulk
  read of the entire store per job (`GetAllBySymbolAsync`); writers only
  append (weekly sync, delisted backfill). No joins, no updates, no per-row
  queries — yet it consumes most of the Basic tier's 2GB cap and its
  whole-table read outgrew even a 300s command timeout (now read in four
  partitioned passes).
- **The 2GB cap is the gate on the next strategic move**: extending the
  dataset back through 2000–2010 (dot-com + GFC) so the validation holdout
  finally contains real convulsions — the fix for "nothing holds up
  out-of-sample" (see docs/regime-setups-plan/results.md).
- Blob storage at this scale costs **pennies/month**, has no meaningful cap,
  and a parallel blob download beats a 5-DTU table scan.

Cosmos DB was considered and rejected: its strengths (point reads,
partitioned queries) are things this workload never does, and bulk loads are
RU-throttled. Blob is the natural home for immutable per-symbol history.

## Design

### Layout (container `historic-candles`)

- `bars/{SYMBOL}.json.gz` — gzipped JSON array of `[date, o, h, l, c, v]`
  rows, sorted by date, deduped. One blob per symbol.
- `meta.json` — small index: dataset version, last-backfill stamp, and per-
  symbol `{min, max, count}`. Serves GetLatestDates / GetEarliestDates /
  Count / MaxDate without touching a single bar blob. Rewritten at the end
  of each write batch; writers are already serialized (single sync/backfill
  consumer), so no concurrency protocol is needed.

### Code

- `BlobHistoricalCandleRepository` (Infrastructure/Storage) implements the
  EXISTING `IHistoricalCandleRepository` — no caller changes anywhere.
  - Bulk read: parallel blob downloads (bounded), assembled into the same
    per-symbol dictionary shape.
  - `AddRangeAsync`: per symbol, download-merge-upload (append-mostly, so
    the merge is cheap); meta updated once per batch.
  - `GetDatabaseSizeMbAsync` returns 0 — blob mode has no size cap, which
    deliberately disarms the delisted-backfill 1600MB gate.
  - Dataset version lives in `meta.json` (fingerprints keep working).
- Pure serialization/merge logic in a static `CandleBlobCodec` (unit-tested
  without any Azure dependency).
- **Config switch**: `HistoricStore:UseBlob` (default **false** — SQL
  stays authoritative until the migration is verified). Connection:
  `HistoricStore:BlobConnection`, falling back to `AzureWebJobsStorage`
  (the Functions storage account). The API needs one of these set before
  the flag flips.

### Migration (SQL → blob)

- New `Mode: "blobmigrate"` on the existing `candlesync-jobs` queue —
  same chunked-continuation pattern as the delisted backfill (each chunk a
  fresh message, so delivery counts reset and a host restart costs one
  chunk). Reads per-symbol from SQL (targeted queries, Basic-tier safe),
  writes blobs, skips symbols already migrated (resumable/idempotent).
- Triggered from a Strategy Lab endpoint (`POST /strategy-lab/blob-migrate`).
- Completion writes an activity entry with a SQL-vs-blob bar-count
  comparison — the verification gate before flipping `UseBlob`.

### Cutover sequence

1. Deploy (flag off — zero behaviour change).
2. Trigger migration; wait for the completion activity entry; check counts.
3. Set `HistoricStore:UseBlob=true` (+ `HistoricStore:BlobConnection` on
   the API) and restart. SQL table stays as a fallback until confidence,
   then `TRUNCATE TABLE HistoricalCandles` frees ~1GB of the 2GB cap.
4. Weekly sync / backfills now write blobs; the 2000-era backfill becomes
   a follow-up spec unblocked by this one.

### Out of scope

- Deleting the SQL table/migrations (manual, after verification).
- The 2000–2010 backfill itself (needs its own spec: Tiingo coverage that
  far back, screening rules for a very different market, split handling).
