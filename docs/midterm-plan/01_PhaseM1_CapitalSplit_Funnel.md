# Phase M1 — Capital Split, Entities, and the Mid-Term Funnel

**Status: Planned**

**Objective:** the plumbing everything else stands on: the cash-allocation split
between strategies, the mid-term data model (thesis as a first-class entity, with
the SPY scorecard fields from day one), and the quantitative funnel that takes the
~1,500-symbol universe down to ~80 mid-term candidates. No Claude calls in this
phase.

## ⚠ VERIFY at build time

- ⚠ Fundamental data coverage: the funnel needs revenue/earnings growth for the
  whole universe, not just watchlist symbols. Check what FundamentalDataService
  actually fetches per symbol and what Finnhub's rate limit (50/min) implies for a
  1,500-symbol sweep — if a full sweep takes >30 min, cache fundamentals weekly
  (they change quarterly; daily freshness is waste).
- ⚠ 12-month relative-strength inputs: HistoricalCandles now holds 5y of daily
  bars for the whole universe + sector ETFs — confirm coverage % before relying on
  it (symbols recently added to indexes will have gaps; they fail open to
  exclusion, not to SPY-relative).
- ⚠ Confirm CapitalRules bounds for the new allocation dials (0–100%, sum ≤ 100).

## Design

### 1. Capital split (Settings → Trading)

Two new `AccountRiskProfile` fields + migration:

- `MidTermAllocationPct` (default **0.0** — feature dormant until the user
  allocates) and the implicit swing share = `1 − MidTermAllocationPct`. One dial,
  not two, so they can't disagree.
- **Swing side enforcement:** ExecutionService's sizing already computes
  `availableCash`; multiply by the swing share before sizing. One line, tested.
- **Mid-term side guidance:** the mid-term page shows
  `available cash × MidTermAllocationPct − (linked open mid-term positions value)`
  as "budget remaining" — advisory (the human executes), but always visible.

### 2. Entities + migration (`AddMidTermThesis`)

```
MidTermThesis
  Id, AccountId, Symbol, CompanyName, Status
    (Proposed | Active | Closed | Rejected | Expired)
  -- the contract, written at selection time, never edited:
  ThesisSummary        nvarchar(2000)   -- why this, in plain English
  GrowthDriver         nvarchar(500)    -- the structural trend it rides
  ExpectedValuePct     decimal          -- e.g. 0.35 = +35% expected
  HorizonMonths        int              -- 1..12
  InvalidationJson     nvarchar(2000)   -- list of observable conditions
  ProposedAt, PriceAtProposal
  -- SPY scorecard anchors (day one, per Overview):
  SpyAtProposal        decimal
  -- link/lifecycle (Phase M3/M4 fill these):
  LinkedAt, EntryPrice, ClosedAt, ExitPrice, CloseReason
MidTermReview          -- Phase M4 rows; table created now so the FK exists
  Id, ThesisId, ReviewedAt, Verdict, Reasoning, PriceAtReview, SpyAtReview
```

Scorecard view = computed from these anchors; no separate table.

### 3. The funnel (1,500 → ~80): `MidTermScreener`

A NEW screener — the swing screener finds daily movers; this finds durable
trends. All inputs come from data already stored (HistoricalCandles, sector map,
cached fundamentals). Deterministic, no Claude. Filters, then rank:

- **Trend intact:** price above its 200-day average AND 6-month return positive.
- **Sector-relative strength:** 6-month return beats its sector ETF's
  (SectorEtfMap.Resolve — same source as everything else).
- **Not parabolic:** within 25% of the 52-week high but not more than 10% above
  the prior 3-month range top (buys trends, not blow-off tops).
- **Growing:** revenue growth positive latest quarter (from cached fundamentals;
  missing data = excluded, never neutral — this is a selection funnel, not a
  scorer).
- **Liquid:** 20-day average dollar volume ≥ $20M (higher than swing's $10M —
  larger positions, longer holds).
- **Rank** the survivors by a simple composite (6m sector-relative return ×
  revenue growth quartile) and take the top 80.

Runs weekly (piggybacks the Sunday watchlist slot timing-wise but is its own
job type, `MidTermScreen`, so failures are independently visible). Output stored
as a `MidTermCandidates` snapshot (symbol, metrics, rank, date) so Phase M2's
selection is reproducible and auditable.

## Test plan

- Allocation: swing sizing budget = cash × (1 − MidTermAllocationPct); 0% =
  byte-identical to today (regression); bounds validation.
- Screener (fixture bars): each filter individually (below 200dma out, sector
  laggard out, parabolic out, negative revenue growth out, illiquid out);
  missing fundamentals = excluded; rank composite ordering; cap at 80.
- Entities: migration round-trip; thesis contract fields immutable via
  repository (no update path for contract columns).

## Acceptance criteria

- Settings shows the allocation dial; setting 30% visibly reduces the swing
  bot's next sizing budget (log line proves it).
- Weekly screener run stores a candidate snapshot; admin jobs page shows the
  new job type; a candidates list is queryable (API endpoint, minimal UI —
  the real page comes with M2).

## Rollback

Allocation defaults to 0% (swing unaffected); screener job is additive; tables
are inert without M2.
