namespace SwingTrader.Core.Models;

// A classified small-cap 8-K event (docs/filing-events-plan P1): platform-
// level (one row serves every account), observed and classified only - no
// trading rides on these until the P2 forward scorecard earns it.
public class FilingEvent : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string Cik { get; set; } = string.Empty;
    // EDGAR accession number - the natural dedup key.
    public string AccessionNumber { get; set; } = string.Empty;
    public DateOnly FiledAt { get; set; }
    // Raw 8-K item codes, comma-separated ("4.02,9.01").
    public string ItemCodes { get; set; } = string.Empty;

    // Claude classification (null EventType = routed but classification
    // failed; kept for retry/audit).
    public string? EventType { get; set; }         // e.g. OfficerDeparture, NonReliance, MaterialAgreement
    public string? Direction { get; set; }         // Bullish | Bearish | Unclear
    public int Severity { get; set; }              // 1-5
    public string? Summary { get; set; }
    public string? Facts { get; set; }             // salient extracted facts, free text

    // Null = cap unknown (P1 approximates small-cap by exclusion from the
    // liquid universe; a real cap source is a recorded follow-up).
    public decimal? MarketCapUsd { get; set; }
    public string? DocumentUrl { get; set; }

    // P2 forward stamps (trading-day windows, filled once elapsed).
    public decimal? FwdReturn5Pct { get; set; }
    public decimal? FwdReturn20Pct { get; set; }
    public decimal? SpyReturn20Pct { get; set; }
    public DateTime? ForwardStampedAt { get; set; }
}
