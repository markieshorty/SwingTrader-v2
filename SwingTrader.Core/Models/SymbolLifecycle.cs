namespace SwingTrader.Core.Models;

// Listing lifecycle for symbols in the survivorship-free historic dataset
// (docs/survivorship-plan P1). Rows exist only for DELISTED symbols whose
// bars were stored by the backfill - the engine and UI read delisting dates
// from here rather than inferring them from bar gaps. Sourced from Tiingo's
// supported_tickers.csv.
public class SymbolLifecycle
{
    public int Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly? ListedAt { get; set; }
    public DateOnly? DelistedAt { get; set; }
    // Why the listing ended ("acquisition", "bankruptcy", ...). The source
    // CSV carries no reason, so this ships null (= unknown) and the P2
    // engine haircut applies; a later enrichment pass can tag acquisitions.
    public string? EndReason { get; set; }
}

// Single-row dataset metadata. DatasetVersion feeds ConfigFingerprint so
// results computed on different dataset generations (v1 survivorship-biased,
// v2+ including delisted symbols) can never silently compare as equal.
public class HistoricalDatasetInfo
{
    public int Id { get; set; }
    public int DatasetVersion { get; set; } = 1;
    public DateTime? LastDelistedBackfillAt { get; set; }
}
