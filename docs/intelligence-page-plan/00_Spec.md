# Intelligence Page — Spec

**Status: Spec agreed with Mark, 14 Jul 2026 (build not started)**

**Objective:** one place in the app to see everything the shadow signals are
producing — the funnel's shadow evidence, filing-language deltas, second-hop
transmissions, and (later) the scorecards. Today this evidence exists only in
the daily report email or in database columns with no surface at all, which
undermines the system's core loop: signals are supposed to *earn* influence
through reviewed evidence, and evidence nobody can comfortably review doesn't
get reviewed.

**Zero new data, zero new Claude cost.** Every tab reads tables and columns
that already exist and are already populated. This is purely read-side: a few
small GET endpoints + one Angular page.

## The page: `/intelligence`, three tabs (v1)

### Tab 1 — Funnel shadow (the F2 flip evidence)

The two-week shadow review currently lives in one line per daily email. This
tab makes it reviewable in one sitting:

- **Headline stats** over a selectable window (default: since F1 deploy):
  signals scored, legacy Buys vs gate would-Buys, divergent count, would-veto
  count — the same numbers as `ComputeFunnelShadowStats`, aggregated across
  days instead of per-day.
- **Divergence table**: every signal where `(Recommendation == Buy) !=
  WouldPassGate`, with date, symbol, legacy conviction, gate score, forward
  score, and what each system would have done. This is THE table Mark reads
  before flipping `Research:FunnelEnabled`.
- **Veto candidates**: gate-passers with `WouldBeVetoed`, with forward-score
  breakdown — mirrors the report's veto section, browsable.
- Data source: `StockSignals` (all fields exist). One endpoint:
  `GET /api/intelligence/funnel-shadow?days=N`.

### Tab 2 — Filings (the FD1 audit trail)

- **Toggle: Opportunities / Warnings.** Long-only nuance baked into the UI:
  Lazy Prices says language-changers *underperform*, so the negative deltas
  are the actionable half. *Warnings* = most negative first, with a badge on
  any symbol that is currently held or is today's Buy/Watch. *Opportunities*
  = most positive first (risks removed, hedging dropped).
- Each row: symbol, delta (with direction × materiality shown separately),
  filed date + filing type, category chips, **the plain-English summary**
  (the audit trail), today's decayed effective score, and an **EDGAR link**
  to the primary document — verifying Claude's reading against the source
  must be one click, same philosophy as the suppressible economic links.
- Unchanged-filing count shown as context ("34 filings checked, 5 changed") so
  a quiet quarter reads as the hash gate working, not the pipeline broken.
- Data source: `Filings` + `FilingDeltas`. One endpoint:
  `GET /api/intelligence/filings?days=N`.

### Tab 3 — Second-hop (the SH1 provenance)

- Recent non-null `SecondHopScore` signals, strongest |score| first: symbol,
  score, and the provenance summary ("AMAT +0.6 via TSMC guidance") — the
  hallucination check happens by reading these.
- Each row links to the symbol's economic-links panel (existing watchlists
  UI) so a suspect transmission is two clicks from its kill switch.
- Data source: `StockSignals`. Endpoint:
  `GET /api/intelligence/second-hop?days=N`.

### Later (not v1)

- **Scorecards tab**: veto counterfactuals, SizeMultiplier-vs-outcome,
  filing-delta and second-hop correlation reads — added when the shadow
  windows have accrued enough data to be worth charting (the same evidence
  gates FD2/SH2 already define).

## Quick wins outside the page (same build)

1. **Qualitative pick rationales**: the archetype + reason stored in
   `WatchlistHistory` surfaces as a tooltip/expandable line on the
   qualitative watchlist's items — the review-before-enable decision is
   currently made blind, which defeats its design. (Small endpoint: latest
   history `Reason` per symbol for the list.)
2. **Trade detail: the contract + the forward evidence.** Open-position and
   closed-trade views gain: frozen-at-entry rules (hold days, probation day,
   trailing shape), `ForwardScoreAtEntry` and `SizeMultiplier`. A position
   should show the deal it's running under.

## Navigation & access

- New nav entry "Intelligence" (icon: insights), any account member can view
  (read-only — nothing on this page mutates anything).
- The daily report keeps its lines (email is the push channel; this page is
  the pull channel) — no report changes.

## Decisions locked (14 Jul 2026)

1. One page, tabs — not scattered pages (matches how the evidence is
   actually consumed: one review sitting).
2. Filings tab leads with the Warnings/Opportunities split; negative deltas
   flag held/Buy symbols explicitly.
3. Every AI-produced claim on the page links to its source (EDGAR document,
   economic-links panel) — audit-in-one-click is the trust mechanism.
4. Read-only, existing data only, no new Claude calls anywhere.
5. Scorecards tab deferred until the shadow windows have data worth charting.
