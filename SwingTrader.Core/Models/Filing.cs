namespace SwingTrader.Core.Models;

// Platform-level SEC filing record (docs/filing-delta-plan) - deliberately NOT
// account-scoped, like HistoricalCandle: a 10-Q is the same document for every
// user, so one copy serves all accounts' filing-delta scores. Synced by
// FilingSync from EDGAR (free, no key; declared User-Agent required).
public class Filing
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Cik { get; set; } = string.Empty;          // zero-padded 10-digit
    public string AccessionNumber { get; set; } = string.Empty;
    public string FilingType { get; set; } = string.Empty;   // "10-K" | "10-Q"
    public DateOnly FiledAt { get; set; }
    public string PrimaryDocument { get; set; } = string.Empty;

    // Extracted, normalized section text + hashes. The hash gate compares
    // hashes against the previous filing of the same type BEFORE any Claude
    // call - unchanged sections cost zero tokens, and that is the design.
    // Null text/hash = section extraction failed for that section (degraded,
    // never fatal); ParseFailed marks the whole document unusable.
    public string? RiskFactorsHash { get; set; }
    public string? RiskFactorsText { get; set; }
    public string? MdaHash { get; set; }
    public string? MdaText { get; set; }
    public bool ParseFailed { get; set; }

    public DateTime CreatedAt { get; set; }
}

// The scored diff between a filing and the previous filing of the same type
// for the same symbol. Delta 0 + null summary = "unchanged" (the common
// copy-paste quarter, detected by hash, no Claude involved).
public class FilingDelta
{
    public long Id { get; set; }
    public long FilingId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateOnly FiledAt { get; set; }                    // denormalized for decay reads

    public decimal Direction { get; set; }                   // -1..+1
    public decimal Materiality { get; set; }                 // 0..1
    public decimal Delta { get; set; }                       // direction * materiality
    public string? Categories { get; set; }                  // comma-separated labels
    public string? Summary { get; set; }                     // human-readable what-changed (the audit trail)
    public string? Model { get; set; }
    public DateTime ScoredAt { get; set; }
}
