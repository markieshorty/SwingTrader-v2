# Phase M4 — Weekly Review Agent, Verdicts, Scorecard v2

**Status: Planned**

**Objective:** the loop that makes this a system instead of a stock-picking
newsletter. Weekly, every Active thesis is evaluated **against its own contract**
(never re-litigated from scratch) and receives one of five verdicts with
plain-English reasoning, delivered on the page and in an email digest. The
scorecard grows into the strategy's actual performance record.

## ⚠ VERIFY at build time

- ⚠ The invalidation-condition checker: conditions were constrained to checkable
  patterns in M2 — verify against the real theses accrued by then; any pattern
  the checker can't evaluate gets flagged "needs human judgement" in the review,
  never silently skipped.
- ⚠ Weekly Claude cost: one call per Active thesis (≤8) — trivial, but confirm
  prompt sizes with real 90-day price/news context.

## Design

### 1. Review job (`MidTermReview`, weekly, Saturday after candle sync)

Two layers per Active thesis, deliberately split:

- **Mechanical layer (code, no Claude):** evaluates each checkable invalidation
  condition against stored data (price levels, MA conditions, revenue growth
  from cached fundamentals) → per-condition TRIPPED / OK / UNCHECKABLE.
  Also computes: return since entry, vs SPY since entry, % of expected value
  reached, % of horizon elapsed.
- **Judgement layer (Claude):** receives the contract, the mechanical results,
  30-day sentiment/news from the archive, and returns a verdict + reasoning:
  - **Hold** — thesis on track;
  - **Add** — thesis strengthening AND budget headroom exists (M1 line);
  - **Trim** — ahead of schedule but stretched (e.g. >80% of EV in <30% of
    horizon);
  - **Sell — matured** — expected value reached (mechanical trigger; Claude
    writes the confirmation, can't overrule it silently);
  - **Cut — thesis broken** — any invalidation condition TRIPPED (mechanical
    trigger; same rule: Claude explains, doesn't veto. If Claude disagrees, the
    verdict stays Cut and the disagreement is shown — the human decides).

The mechanical layer owning Sell/Cut triggers is the honesty core: the LLM
narrates, the contract decides. Horizon expiry (elapsed > HorizonMonths) forces
a final review with a mandatory Sell-or-rewrite decision — theses don't drift
into forever-holds. "Rewrite" = close the old thesis and propose a new one with
a fresh contract, keeping the scorecard continuous.

Each review persists as a `MidTermReview` row (verdict, reasoning, price + SPY
snapshots) — the audit trail mirrors the refinement history pattern.

### 2. Delivery

- Mid-Term page: verdict badge per thesis, expandable history of past reviews
  (the thesis's whole life visible on one card).
- Email digest (existing notification-recipient machinery, new category
  `MidTermReview`): one email, all verdicts, reasoning inline, budget line at
  the top. Only sends when there IS an Active thesis.

### 3. Scorecard v2

- Per closed thesis: realised return vs SPY over the same window, days held,
  verdict history.
- Aggregates: hit rate (closed above SPY), average excess return, and the
  headline the Overview demands: **"all-time mid-term book vs SPY"** — the
  number that eventually answers whether this strategy deserves more capital
  than the index. Shown even when it's unflattering; especially then.

## Test plan

- Mechanical checker: each condition pattern (price level, MA break with
  N-session persistence, metric direction) TRIPPED/OK/UNCHECKABLE fixtures;
  EV-reached and horizon-expiry triggers.
- Verdict precedence: tripped invalidation → Cut regardless of Claude text;
  EV reached → Sell-matured; Claude parse failure → review stored as
  "needs human judgement", never a silent skip.
- Review rows immutable; digest only when Active theses exist; scorecard math
  on fixtures (positive and negative excess).

## Acceptance criteria

- Demo with ≥1 Active thesis: Saturday run produces a review row, page badge,
  and digest email; a thesis with a deliberately tripped price condition gets
  Cut with the condition named.
- Scorecard aggregates visible and correct against hand-computed fixtures.

## Rollback

Job behind `MidTerm:Enabled`; reviews are additive rows; email category is
opt-in per recipient like every other category.
