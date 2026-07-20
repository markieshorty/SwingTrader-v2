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
    public string EdgarUserAgent { get; set; } = "Cadentic/1.0 (contact: mark.ross.short@gmail.com)";
    public int EdgarDelayMs { get; set; } = 250;

    // Deltas predict returns over months (Lazy Prices), so the effective
    // score decays with a ~one-quarter half-life. Trading days are
    // approximated as calendarDays * 5/7 at read time.
    public int HalfLifeTradingDays { get; set; } = 63;

    // Paragraph-level diff cap: only this many added + removed paragraphs are
    // sent to Claude (with the rest summarized as a count), bounding tokens
    // on pathological rewrites. When the cap binds, the LONGEST paragraphs
    // are kept (substance over boilerplate reshuffles). History: briefly
    // tightened to 15 + per-paragraph clipping after FD1's backfill produced
    // a large first-day bill on Opus; restored to full fidelity 14 Jul 2026
    // ("I want it to do it properly" - Mark) once the premium model moved to
    // Sonnet, whose pricing makes an uncapped changed-filing diff a few
    // pence rather than tens.
    public int MaxDiffParagraphs { get; set; } = 40;

    // Stored section text cap (per section, chars) - filings can run to
    // megabytes; we keep enough for the next diff, not the whole document.
    public int MaxSectionChars { get; set; } = 400_000;

    // Cost control on the diff-scoring Claude call - the ONLY token cost in
    // this job (Mark, 15 Jul 2026, after a $12 / 3.3M-token backfill night:
    // "I assumed these would calm down"). The spend is dominated by first-time
    // backfills of newly watchlisted symbols, whose most-recent filing is
    // often months old - and a delta decays with a ~one-quarter half-life, so
    // a stale diff enters the funnel at a fraction of its weight anyway. So we
    // tier the fidelity to the filing's age at scoring time:
    //   age <= FreshScoringDays   -> PremiumModel (Sonnet): a genuinely new
    //                                filing, scored properly.
    //   age <= MaxScoringAgeDays  -> Model (Haiku): backfill fidelity at a
    //                                fraction of the token cost.
    //   older                     -> NOT scored: the filing is still STORED as
    //                                a baseline (free - no tokens), so the NEXT
    //                                quarter's fresh filing gets a full Sonnet
    //                                diff against it. We just don't pay to score
    //                                a stale diff nobody will meaningfully weight.
    public int FreshScoringDays { get; set; } = 30;
    public int MaxScoringAgeDays { get; set; } = 120;

    // Distress quarantine (FD3): a rules-based flag (8-K item 3.01/1.03/4.02
    // or going-concern language in a 10-K/10-Q) blocks new Buys and exits open
    // positions in the symbol for this many days after the filing date.
    // ~90 days spans a typical exchange cure period and one earnings cycle.
    public int DistressWindowDays { get; set; } = 90;
}
