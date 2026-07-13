# Filing Delta Score — Spec

**Status: Spec agreed with Mark, 13 Jul 2026 (build not started)**

**Objective:** score the **change** in a company's own official language between
consecutive SEC filings — risk factors rewritten, hedging added to MD&A,
metrics quietly dropped from the earnings release — and feed it into the
funnel's Forward side. Companies write filings by copy-paste; when counsel or
the CFO *changes* the words, that is management signalling before the numbers
do, and almost nobody reads the diff.

## Why (evidence base)

1. **The academic prior is unusually strong.** "Lazy Prices" (Cohen, Malloy &
   Nguyen, Journal of Finance 2020): firms that change the language of their
   10-K/10-Q underperform non-changers by ~30-60bp/month for months after the
   filing, and the market reaction at filing time is near zero — the signal is
   real and *slow*, so a daily pipeline is more than fast enough.
2. **It is LLM-native.** "Diff two risk-factor sections and explain what
   changed, in which direction, and how material it is" is exactly the shape of
   task Claude is good at and traditional quant pipelines are bad at. This is
   one of the few places a solo operator has tooling parity with (or an edge
   over) institutional pipelines built before 2023.
3. **The data is free and fast.** SEC EDGAR full-text is public, filings appear
   within minutes of acceptance, and the API needs no key (10 req/s cap,
   declared User-Agent). No new data spend.
4. **The cost profile is inverted in our favour.** Most filings are copy-paste
   quarters — a cheap text hash detects "nothing changed" **without any Claude
   call**. Tokens are only spent when the text actually changed, and the change
   *is* the signal. Estimated Claude volume: ~25 watchlist symbols x 4-5
   filings/year that actually differ = a few calls per week.

## The score

### FilingDeltaScore — per (symbol, filing), -1..+1 + materiality

For each new comparable filing, extracted sections are diffed against the same
sections of the previous filing of the same type:

| Filing | Sections compared | Cadence |
|---|---|---|
| 10-K / 10-Q | Item 1A Risk Factors; MD&A | quarterly per symbol |
| 8-K | item codes + body vs recent 8-K baseline | episodic |
| Earnings release (EX-99.1 on 8-K) | headline metrics present/absent, guidance language | quarterly |

Pipeline per filing:

1. **Fetch + extract** sections (EDGAR document, HTML → normalized plain text).
2. **Hash gate:** normalized section hash equals previous filing's → delta 0,
   **no Claude call**, store `Unchanged`. This is the common case and the cheap
   half of the whole design.
3. **Claude diff** (changed sections only): returns
   - `summary` — what changed, in plain English (persisted; this is the audit
     trail and the report content),
   - `direction` — -1..+1 (new risk disclosed / hedging added = negative;
     risk removed / language de-hedged = positive),
   - `materiality` — 0..1 (boilerplate reshuffle vs new customer-concentration
     clause),
   - `categories` — e.g. litigation, liquidity, customer-concentration,
     guidance-language, going-concern.
4. **Score:** `delta = direction * materiality`, persisted with the filing.

### Effective score at research time — decayed, months-scale

Deltas predict returns over **months**, not days. The research pipeline reads
the symbol's most recent non-zero delta and applies exponential decay:

```
effective = delta * 0.5 ^ (tradingDaysSinceFiling / HalfLifeTradingDays)
```

`FilingDelta:HalfLifeTradingDays` default **63** (~one quarter). Unchanged
filings refresh nothing; the previous delta keeps decaying. No filing history
at all → null (degraded semantics identical to the other forward inputs:
contributes neutral, never vetoes).

## Integration — Forward side only, never the Gate

Consistent with the funnel's contract (unbacktestable forward information
scales and vetoes, never gates):

```
forward01 = ForwardSentimentWeight   * sentimentComponent
          + ForwardFundamentalWeight * fundamentalMomentum
          + ForwardFilingWeight      * filingDeltaComponent   // NEW, default 0
```

`filingDeltaComponent = 0.5 + effective/2` (maps -1..+1 onto the 0..1 component
scale). `ForwardFilingWeight` default **0** — shipping the pipeline changes
nothing until the shadow evidence earns it a weight (weights renormalized in
config when raised, must sum to 1).

**Horizon note (explicit):** the months-scale decay means this signal is
partially wasted on the 3-30 day swing book. It is speced here because (a) the
veto case — a bad delta blocking a Buy — works at any horizon, and (b) it is a
foundation input for the future slower book (idea #4, to be speced separately).

## Storage & plumbing (platform-level, like HistoricalCandles)

- **`Filing`** — CIK, symbol, accession number, type, filedAt, per-section
  normalized-text hash, extracted section text (compressed; capped per
  section). One copy for all accounts.
- **`FilingDelta`** — filingId, symbol, direction, materiality, delta,
  categories, summary, model, scoredAt. `Unchanged` rows carry delta 0 and no
  summary.
- **Symbol→CIK map** from EDGAR `company_tickers.json`, cached, refreshed
  weekly.
- **FilingSync job** — new platform-level scheduled job (daily, after market
  close ET; queue `filingsync-jobs`) polling the EDGAR submissions API for the
  union of all accounts' watchlist symbols; fetches new filings, runs the hash
  gate, queues Claude diffs. Uses the platform Claude key. Same idempotency
  pattern as CandleSync.
- **Signal fields:** `FilingDeltaScore` (decimal?), `FilingDeltaSummary`
  (string?), persisted on StockSignal at research time — the shadow record.

## Phases (each observable before the next touches money)

| Phase | Behaviour change | Gate to advance |
|---|---|---|
| **FD1 Shadow** | None. FilingSync live; one-time backfill of the previous 2 filings per symbol (so day-one deltas exist); FilingDeltaScore persisted on every signal; daily report line (`Filing deltas: N scored, worst: XYZ -0.42 "added customer-concentration risk"`). | ≥40 non-zero deltas scored AND sign-correlation with 20d/60d market-adjusted forward returns reviewed. |
| **FD2 Blend** | `ForwardFilingWeight` raised from 0 (config); delta flows into sizing/veto via the existing Forward machinery. | Scorecard shows the blend improving Forward-vs-outcome correlation, not diluting it. |
| **FD3 Tune** | Half-life, materiality floor and weight tuned from evidence; consider 8-K velocity (filing *frequency* spike) as a second feature. | Quarterly review cadence, same as the veto floor. |

## Risks / mitigations

- **HTML section extraction is brittle** (issuers' formats vary wildly).
  Mitigate: extraction failure → store filing with `ParseFailed`, no score,
  degraded-null downstream; never a failed research run. Start with the ~25
  watchlist symbols where formats can be eyeballed, not the 1,500 universe.
- **Sections exceed the context window** (some Item 1As are 100+ pages).
  Mitigate: diff at paragraph level first (cheap, local) and send Claude only
  changed paragraphs with surrounding context, not whole sections.
- **Amendments and fiscal quirks** (10-K/A, transition periods): compare
  amended filings against the same accession they amend, not the prior
  quarter; log-and-skip anything unclassifiable.
- **Sparse cadence** (one 10-Q per symbol per quarter): expected — this signal
  is a slow drip by design. The report line keeps it visible so a quiet month
  isn't mistaken for a broken pipeline.
- **LLM over-reading boilerplate**: materiality field + a prompt instruction
  that legal reshuffles score 0; FD1's human-readable summaries make
  false positives cheap to audit before FD2 gives them money.

## Decisions locked (13 Jul 2026)

1. Change-based, never level-based: unchanged text is always delta 0.
2. Hash gate before any Claude call — tokens are spent only on real changes.
3. Forward-side integration behind `ForwardFilingWeight` default 0; shadow
   first; degraded/null never vetoes. The Gate score is untouched.
4. Platform-level data (one copy, platform keys), watchlist-union scope.
5. Months-scale decay (half-life 63 trading days) — explicitly also a
   foundation for the future slower book.
