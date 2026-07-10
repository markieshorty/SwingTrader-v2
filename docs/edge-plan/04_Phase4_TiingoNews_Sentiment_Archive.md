# Phase 4 — Tiingo News + Proprietary Sentiment Archive

**Status: Planned**

**Objective:** two things. (a) Improve the live sentiment component's inputs by
adding Tiingo's ticker-tagged news feed alongside Finnhub. (b) Start the clock on a
proprietary per-symbol daily sentiment history — the one dataset money cannot buy
retroactively — so that in 6–12 months sentiment becomes analysable (refinement
correlations on real scores, eventually own-data-style replay) instead of forever
unverifiable.

## Verified assumptions (citations)

- Live sentiment today (`ResearchPipeline.FetchAndScoreSentimentAsync`, ~line 265):
  Finnhub company news over `NewsLookbackDays`, take `MaxNewsArticles` newest, one
  Claude call returns a JSON score; persisted on the signal as `SentimentScore` +
  `NewsSummary`; component score persisted as `SentimentComponentScore` (null when
  unavailable — honest-null convention already in place). "No news" is a genuine
  0.0-neutral, fetch/Claude failure is null.
- `StockSignal` rows are already a partial archive: one row per symbol per day WITH
  the sentiment score — but only for symbols on that day's watchlist union, and only
  the blended score + a summary string, not the underlying articles.
- Tiingo News API (Power): included; `?tickers=aapl,googl` filtering; fields id,
  title, url, description, publishedDate, crawlDate, source, tickers, tags; **no
  sentiment field** (we score it ourselves — good, that's our edge); **3 months
  queryable history max** on Power; 8–12k articles/day firehose overall.
- Claude sentiment call cost is unchanged: same one call per symbol — the articles
  block in the prompt just gets richer.

## ⚠ VERIFY at build time

- ⚠ Tiingo News endpoint path + auth for Refit (`/tiingo/news`?) and the actual JSON
  payload — build the DTO from a captured real response.
- ⚠ Per-request article limits/pagination (docs didn't state) — capture from a real
  call; we only need ~10–20 newest per ticker so pagination likely irrelevant, but
  confirm the default page size covers it.
- ⚠ Deduplication across sources: Finnhub and Tiingo will both carry the same PR
  headlines. Dedup by normalised title similarity or URL host+path before the prompt
  so Claude doesn't double-weight one story. Verify how much overlap actually occurs
  with a real sample; pick the dedup key from evidence.
- ⚠ Storage growth estimate: ~90 watchlist symbols × ~5–15 articles/day of metadata.
  Confirm rows/day from a real run before deciding retention (below).

## Design

1. **New client**: `GetNewsAsync(tickers, startDate, limit)` on `ITiingoClient` +
   DTO from captured payload.
2. **Blend, don't replace.** `FetchAndScoreSentimentAsync` fetches Finnhub (as
   today) AND Tiingo news, dedups, orders by recency, takes `MaxNewsArticles`
   (consider raising 10 → 15 with the second source), tags each line with its
   source in the prompt. Failure of EITHER source degrades to the other; both
   failing → null score exactly as today. No change to the score contract.
3. **`SentimentArchive` table** (new entity + migration):
   `Id, AccountId?, Symbol, Date, Source (Finnhub|Tiingo), Title, Url,
   PublishedAtUtc, Description(nvarchar 1000 truncated)` — plus one summary row type
   or a parallel `SentimentDailyScore` table:
   `Symbol, Date, Score, ArticleCount, Model` capturing the Claude blend per
   symbol/day (denormalised from StockSignal so it survives independent of signal
   pruning, and covers symbols scored but below watch threshold).
   *Decision at build time (record here): one table with a discriminator vs two
   narrow tables — prefer two; article metadata and daily scores have different
   lifetimes.*
   - Archive writes are **fire-and-forget** relative to research: an archive INSERT
     failure logs a warning and never fails the research run.
   - Account-scoping: scores are per-account (weights differ… no — sentiment score
     is pre-weights, symbol-level). Store account-agnostic (`AccountId` null) keyed
     on (Symbol, Date, Source-set) to avoid duplicating per account; ⚠ verify the
     multi-account dedup at build time (two accounts researching the same symbol the
     same day must not double-insert — unique index on (Symbol, Date) for scores).
4. **Retention**: articles table pruned at 24 months (config), scores kept forever
   (tiny). Nightly cleanup piggybacks an existing scheduled job.
5. **Explicitly NOT in this phase**: using the archive for backtesting/refinement.
   That's the harvest, 6–12 months out; this phase only plants. No UI beyond a
   count on the admin monitoring page ("sentiment archive: N scores, M articles,
   oldest date") so growth is visible.

## Test plan

- DTO deserialisation from captured Tiingo payload (fixture checked in).
- Dedup: same story from both sources → one prompt line (test the chosen key:
  identical URL; near-identical title).
- Blend degradation matrix: Tiingo fails → Finnhub-only (today's behaviour);
  Finnhub fails → Tiingo-only; both fail → null score, "unavailable" summary; both
  empty → 0.0 "No recent news found."
- Archive: research run persists article metadata + one score row per symbol/day;
  unique-index conflict on second account's same-day run is swallowed (no dupes, no
  research failure); archive DB failure does not fail research (substitute throwing
  repo).
- Retention job deletes only >24-month articles, never scores.

## Acceptance criteria

- Demo research run: signals show sentiment sourced from both feeds (NewsSummary
  mentions counts per source); archive tables populate; admin page shows counts.
- Kill Tiingo key → research still completes with Finnhub-only sentiment (log
  proves the degradation path).

## Rollback

Blend behind `Research:TiingoNewsEnabled` (default true after Demo validation);
archive tables are additive and inert if the flag is off.
