# Phase M2 — Claude Selection with Structured Theses

**Status: Planned**

**Objective:** the monthly pick. Claude reads the 80 funnel candidates (with their
metrics, fundamentals, and recent sentiment from the archive) and proposes N
configurable picks (1–8), each as a **structured thesis contract**. A new
Mid-Term page shows proposals with plain-English reasoning. The first cycle runs
as a paper **shadow book** — theses accrue scorecard history without any money.

## ⚠ VERIFY at build time

- ⚠ Prompt size: 80 candidates × metrics + sentiment summaries must fit the
  model's practical context comfortably — measure a real assembled prompt; if
  large, send the top 40 by rank (funnel already ordered them).
- ⚠ Sentiment archive depth at build time: it only started accruing 11 Jul 2026.
  If <30 days of history, the prompt says so explicitly rather than presenting
  thin data as signal.
- ⚠ JSON contract discipline: reuse the LabAnalysisPrompts pattern (strict JSON,
  fenced-JSON tolerant parser, invalid → advisory-only degradation).

## Design

### 1. Selection job (`MidTermSelect`, monthly, 1st trading day, after research)

Input per candidate: funnel metrics, fundamental snapshot (growth, margins),
last-30-day sentiment scores + article counts (the archive's first harvest),
sector, and the account's current active theses (so Claude avoids duplicating
exposure). One Claude call.

Output per pick (strict JSON):

- `symbol`, `thesisSummary` (plain English, ≤3 sentences),
- `growthDriver` — the structural trend, named specifically ("datacenter power
  buildout", not "AI"),
- `expectedValuePct` + `horizonMonths` (1–12),
- `invalidation` — 2–4 **observable** conditions (price levels, "revenue growth
  turns negative", "loses the 200-day for 10 sessions") — the review agent must
  be able to CHECK these, so the prompt forbids vague ones ("sentiment worsens"),
- `positionSizeSuggestionPct` of the mid-term budget, sector-diversity respected
  (max 2 picks per GICS sector, enforced in code after parsing, not trusted to
  the prompt).

Picks land as `MidTermThesis` rows with Status=Proposed, `PriceAtProposal` and
`SpyAtProposal` snapped from live quotes at persist time (the scorecard anchor —
fail the pick, not the job, if a quote is unavailable).

Config: `MidTerm:MaxPicks` (default 5, range 1–8, Settings dial), monthly refresh
also proposes **replacements only when a slot is free** (closed/rejected theses) —
it never churns active theses.

### 2. Mid-Term page (new nav item)

- **Proposals**: thesis cards — summary, growth driver, expected value, horizon,
  invalidation list, suggested size against the M1 budget line. Buttons:
  **Accept (I'll buy this)** → Status stays Proposed but pinned awaiting the M3
  link (the page explains: buy it at T212, the app will spot it); **Reject** →
  Status=Rejected with the audit kept.
- **Shadow book toggle** (default ON for the first cycle): accepted theses are
  tracked scorecard-wise from `ProposedAt` even if never bought — this is the
  paper-first discipline from the Overview.
- **Scorecard strip**: per thesis, price return since proposal vs SPY since
  proposal, running average across all proposals ever (including rejected ones'
  counterfactual — the honest version: did rejecting help?).

## Test plan

- Parser: valid JSON → theses; fenced JSON; vague invalidation strings rejected
  (regex allowlist of checkable patterns: price level, MA condition, metric
  direction, date-bound events); >MaxPicks truncated by rank; >2 per sector
  trimmed.
- Anchors: proposal persists price+SPY snapshot; quote failure drops that pick
  with a logged warning, others persist.
- Monthly job dedup via the standard JobLog pattern; replacement-only-when-free
  logic (active theses never superseded).
- Scorecard math: pick vs SPY from anchors, fixture prices.

## Acceptance criteria

- A real selection run in Demo produces ≤MaxPicks theses with checkable
  invalidation conditions; the page renders them; Accept/Reject round-trips.
- Scorecard shows movement after a few days (vs SPY), including for rejected
  proposals.

## Rollback

Job disabled via config (`MidTerm:Enabled`, default false until M2 verified);
page hidden behind the same flag.
