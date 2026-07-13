namespace SwingTrader.Core.Models;

// One edge of the second-hop economic graph (docs/second-hop-plan): a company
// economically linked to a watchlist symbol, built by Claude and deliberately
// HUMAN-AUDITABLE - every link carries a rationale, is visible in the UI, and
// can be individually suppressed. Platform-level (not account-scoped): the
// economic graph is the same for everyone.
public class EconomicLink
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;        // the watchlist target
    public string LinkedName { get; set; } = string.Empty;
    public string? LinkedTicker { get; set; }                 // null = private company (context only, unscoreable)
    public string Relation { get; set; } = string.Empty;      // Supplier | Customer | Competitor | SharedChain
    // Which direction good news flows, e.g. "TSMC capacity constraints are
    // NEGATIVE for NVDA but POSITIVE for competing foundries".
    public string TransmissionNote { get; set; } = string.Empty;
    public decimal Strength { get; set; }                     // 0..1
    public string Rationale { get; set; } = string.Empty;

    // The human kill switch: a suppressed link is kept (audit trail) but
    // never feeds the relevance pass. Hallucinated links must be one click
    // to neutralize, not a database surgery.
    public bool Suppressed { get; set; }

    public DateTime BuiltAt { get; set; }
    public string? Model { get; set; }
}
