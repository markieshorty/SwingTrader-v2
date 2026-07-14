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
    // on pathological rewrites. Lowered from 40 (13 Jul 2026): at 40, one
    // symbol's first-ever backfill (2 sections x 2 filing types x up to 80
    // paragraphs each) could plausibly reach tens of thousands of prompt
    // tokens, and that ran for every watchlist symbol at once on rollout - a
    // real contributor to an unexpectedly large first-day API bill. 15 is
    // still generous for judging materiality; a genuinely sprawling rewrite
    // degrades to "many paragraphs changed" rather than reading all of them.
    public int MaxDiffParagraphs { get; set; } = 15;

    // Stored section text cap (per section, chars) - filings can run to
    // megabytes; we keep enough for the next diff, not the whole document.
    public int MaxSectionChars { get; set; } = 400_000;
}
