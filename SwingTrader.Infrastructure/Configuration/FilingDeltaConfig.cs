namespace SwingTrader.Infrastructure.Configuration;

// Filing-delta score knobs (docs/filing-delta-plan). The signal itself ships
// SHADOW-ONLY: it drives nothing until the funnel's ForwardFilingWeight (see
// ResearchConfig, Phase FD2) is raised above its 0 default.
public class FilingDeltaConfig
{
    public const string SectionName = "FilingDelta";

    // Master switch for the FilingSync job (the shadow scoring in research
    // degrades to null when no data exists, so disabling this is always safe).
    public bool Enabled { get; set; } = true;

    // EDGAR requires a declared User-Agent identifying the caller; anonymous
    // requests are blocked. Fair-use cap is 10 req/s - we stay far under it.
    public string EdgarUserAgent { get; set; } = "SwingTrader/1.0 (contact: mark.ross.short@gmail.com)";
    public int EdgarDelayMs { get; set; } = 250;

    // Deltas predict returns over months (Lazy Prices), so the effective
    // score decays with a ~one-quarter half-life. Trading days are
    // approximated as calendarDays * 5/7 at read time.
    public int HalfLifeTradingDays { get; set; } = 63;

    // Paragraph-level diff cap: only this many added + removed paragraphs are
    // sent to Claude (with the rest summarized as a count), bounding tokens
    // on pathological rewrites.
    public int MaxDiffParagraphs { get; set; } = 40;

    // Stored section text cap (per section, chars) - filings can run to
    // megabytes; we keep enough for the next diff, not the whole document.
    public int MaxSectionChars { get; set; } = 400_000;
}
