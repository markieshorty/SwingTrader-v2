# Qualitative AI Watchlist — Spec

**Status: Spec agreed with Mark, 13 Jul 2026 (built same day)**

**Objective:** a second AI-managed watchlist whose picks come from Claude's
*qualitative* judgement over the whole screening universe — hype/crowd
favourites, structural growers, turnarounds, catalyst-rich names — rather
than from the technical screener. Same weekly lifecycle as its sibling; the
funnel gate remains the only thing that turns a pick into a trade.

## Why

1. **A lens the screener structurally lacks.** The existing AI list picks
   from technically pre-screened survivors; nothing in price/volume data can
   express "this is a crowd favourite" or "this is a secular compounder".
   Claude's world knowledge is exactly the input the screener can't have.
2. **One bucket, not categories — for evaluability.** One qualitative list
   yields ONE testable hypothesis: do narrative-driven picks generate better
   signals than screener picks? Category buckets of 5-10 names each would
   never reach significance at this trade volume. Archetypes survive as
   PER-PICK LABELS (stored in the pick's rationale/history), so if the tags
   later show one archetype outperforming, the evidence can earn it a
   dedicated bucket — categorize later, when data demands.
3. **Contained risk.** Picks only feed research; the gate decides entry, the
   forward score sizes it. A bad pick costs API calls, never money. The list
   is created **disabled** — picks are reviewable (with rationales) before
   they cost anything.

## Design

- **`WatchlistType.AiQualitative`** — one per account, name "Claude
  Qualitative", created disabled on first refresh, refreshed by the same
  weekly Sunday Watchlist job immediately after the technical selection.
- **Selection**: one Claude call (premium model — see Opus note below) over
  the full universe (symbol + company name pasted in), instructed to span
  archetypes (hype momentum / structural growth / turnaround / catalyst-rich
  / fallen angel) and label each pick, grounded with recent context the
  platform already collects (the sentiment archive's strongest movers).
  "Likely to be active" comes from this week's data, not model memory.
- **Server-side validation**: picks not in the universe are dropped
  (hallucinated tickers are a certainty, not a risk); picks already on any
  enabled watchlist are skipped (same rule as the technical list).
- **Apply**: diff against the list's current items with open-position
  protection (a symbol with an open trade is never removed), archetype +
  rationale recorded per pick in WatchlistHistory. Size:
  `Watchlist:QualitativeSize` default **10** (a themed list is a probe, not
  a portfolio); master switch `Watchlist:QualitativeEnabled` default true.
- **Caps**: an account's total enabled-symbol cap (100) applies unchanged
  when the user enables the list — enabling is the moment the picks start
  costing research.

## Evaluation

The A/B is watchlist-level: compare gate-pass rates, forward scores and
trade outcomes of symbols sourced by the qualitative list vs the technical
list over ≥2 months. History rows carry the archetype labels for the later
per-archetype read. No new scorecard machinery needed — signals and trades
already record everything by symbol.

## Premium-model note (same commit)

High-value, low-frequency Claude paths move to **Opus** via a new
`Claude:PremiumModel` knob (default `claude-opus-4-8`): watchlist selection
(both lists), refinement analysis, Lab analysis + sweep explanation, filing
diff scoring, economic-link graph building, and the second-hop relevance
pass. High-volume structured extraction (per-symbol sentiment/catalyst,
fundamentals, bellwether levels) stays on the cheap default model —
intelligence where judgement is scarce, economy where volume is high.
