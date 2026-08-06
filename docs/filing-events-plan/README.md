# Small-Cap Filing Events

Status: **SPEC — drafted 6 Aug 2026.**

## Thesis (why this is the right hunting ground)

US disclosure law forces material events into public documents within days —
for every listed company, including the ~half of sub-$500M companies with
ZERO analyst coverage. On large caps the market digests a filing in minutes;
on neglected small caps the reaction is measurably slow and partial
(post-event drift is several times larger in low-coverage names). Reading
the long tail of filings was historically uneconomic for humans - which is
exactly why the inefficiency persists, and exactly what an LLM pipeline
changes. This is the one direction where Cadentic's specific assets (Claude
integration, shadow-evidence discipline, EDGAR plumbing already built for
FD1-3) are the edge, rather than commodity indicators competing with quant
funds.

Event-driven also sidesteps the curve-fitting trap that killed three
four-digit backtests this week: each event type is a discrete, pre-declared
hypothesis with a timestamp and a defined forward window - measured on
FORWARD data as it accumulates, not swept against a biased history.

## What it is NOT

- Not a backtest-first strategy: our historic dataset has no filing history
  and its universe bias (docs/sleeves-plan P2a verdict) makes historical
  event studies on it dishonest. Evidence accumulates FORWARD, like the
  funnel scorecard.
- Not a trader (yet): P1 observes and classifies only. Real orders are P3,
  gated on P2 evidence.
- Not the existing filing-delta system: FD1-3 score OUR watchlist's 10-K/Qs
  as a signal component. This watches the WHOLE small-cap tape for 8-K
  events. Shared plumbing, different question.

## Architecture

### P1 — the event feed (observe + classify, no trading)

1. **Market-wide poller**: EDGAR daily index (one request per day lists
   every filing by form type) -> all 8-Ks. No per-company polling.
2. **Mechanical routing (zero tokens)**: parse each 8-K's item codes from
   the submissions metadata. Only a pre-declared set proceeds:
   - 4.02 non-reliance on financials (the fire alarm)
   - 5.02 director/officer departure (CEO/CFO especially)
   - 3.01 delisting/listing-standard notice
   - 1.03 bankruptcy/receivership
   - 2.01 completed acquisition/disposition
   - 1.01/1.02 material agreement entry/termination
   - 5.07 (voting) and pure earnings 2.02/9.01-only filings are DROPPED.
3. **Small-cap filter**: company market cap <= $500M (Finnhub profile,
   cached per symbol ~30 days; symbols without cap data are kept but
   flagged). Symbols already in the liquid-1500 universe are allowed (some
   overlap at the boundary is fine).
4. **Claude classification** (the only metered step): for routed filings,
   extract text (existing FilingTextExtractor) and classify into an event
   record: type, direction (bullish/bearish/unclear), severity 1-5, a
   two-sentence plain-English summary, and salient facts (who departed,
   what was terminated, deal size). Model: Sonnet. Strict JSON.
5. **FilingEvent table + UI**: a feed on the Intelligence page (newest
   first, filter by type/direction), each event linking to the EDGAR doc.
   Forward returns are stamped later by P2.

**Token cost (stated up front per house rule):** after mechanical routing,
expect ~30-80 classifiable 8-Ks per trading day market-wide at ~2-3k tokens
each on Sonnet => roughly **£0.30-0.90/day recurring** (~£10-25/month).
Ships behind `FilingEvents:Enabled` (default **false**) so the spend is an
explicit flip. Burst risk is bounded by the daily index (no backfill in P1).

### P2 — the event scorecard (evidence, still no trading)

- A daily job stamps each event with forward returns (+5/+10/+20 trading
  days, raw and SPY-adjusted) from Tiingo EOD once the windows elapse.
- Pre-declared hypotheses, judged ONLY on forward data as n accumulates
  (no mining; new hypotheses must be declared before their data exists):
  - **H-FE1**: severity>=3 BEARISH events (4.02, CFO exit + delay, 3.01)
    show negative 20d drift - actionable as an AVOID/veto overlay for any
    symbol the swing book considers.
  - **H-FE2**: bullish 1.01/2.01 events on sub-$500M names show positive
    20d drift - the long candidate.
  - **H-FE3**: severity calibration - Claude's 4-5 severity events move
    more than its 1-2s (tests whether the LLM adds anything beyond the
    item code).
- Scorecard panel on the Intelligence page per event type x direction:
  n, avg drift, hit rate. Same visual grammar as the forward scorecard.

### P3 — acting on it (gated on P2)

Only if a hypothesis shows discriminating power at n >= ~50 events:
bearish events join the research pipeline as a veto input first (cheapest,
safest); a long book (small position, event-anchored entry/exit) would be
a new sleeve and its own decision. Not designed further here on purpose.

## Candidate research-input additions (NOT to be built yet)

Recorded 6 Aug 2026. Claude's actual scoring role today is narrower than
"conviction scoring" suggests: it scores SENTIMENT (news headlines ->
score + catalyst) and FILING DELTAS (10-K/Q language diffs). Fundamentals
are deterministic by design; the gate score is pure technicals and Claude
never sees it. Four things Claude could additionally be given, ranked:

1. **8-K events on WATCHLIST names** (the one this spec creates). The P1
   scan deliberately excludes the liquid universe, so the classifier never
   reads the events most relevant to the book we actually trade. FD3
   pattern-matches distress item codes as a rules-only veto, but nothing
   READS them. Cheapest real gain: allow the four distress codes through
   for universe names (~1-3 extra classifications/day) and feed the
   classified event into the sentiment call as context.
2. **Sector/peer context** - is this name down alone or is its whole
   sector down? Idiosyncratic vs systematic is central to whether a
   decline is an opportunity. The cross-sectional percentile machinery
   already computes the inputs.
3. **The account's own trade history in the symbol** - "traded PTCT three
   times, all stopped out" is context a human would weigh heavily.
4. **Price context (RSI/drawdown/setup)** - tempting but ARGUE AGAINST:
   the funnel's design separates gate (price) from forward (narrative).
   Feeding price into the sentiment call correlates the two scores and
   collapses the separation that makes the combined score meaningful.

**Why none of these ship now:** the forward-score trial (does it predict
outcomes?) sits at 27 of ~100 trades. Changing what Claude sees changes
the SCORER, which invalidates the banked evidence and resets the clock on
the trial Mark committed to running (memory: run-and-observe, 6 Aug 2026).
Revisit only when that trial renders a verdict - and note that a NEGATIVE
verdict is the strongest argument for adding inputs, with a clean baseline
to change from.

## Reuse

IEdgarClient (+ one new daily-index method), FilingTextExtractor, Claude
client + rate limiter, activity feed, Intelligence page, scheduler/queue
patterns (chunked, resumable, job-log dedup).

## Out of scope (P1-P2)

- Form 4 insider-cluster detection (own spec later - different feed, high
  value but separate parsing).
- 10-K/Q deep reads for non-watchlist names; anything real-time intraday
  (the daily index is end-of-day; drift hypotheses are multi-day).
- Shorting (UK retail cannot; bearish events become vetoes, not trades).
