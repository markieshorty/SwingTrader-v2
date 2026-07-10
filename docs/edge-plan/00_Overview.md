# Current-Time Edge Plan — Overview

**Thesis (owner's):** the price-derived technicals (RSI, MACD, Volume, Setup quality)
are roughly break-even on their own — the backtests say so — and act mainly as a
*veto* against entering trades with indicator warning signs. The edge, if it exists,
lives in the current-time predictors (Sentiment, Fundamental momentum) and in acting
on *fresh* data. This plan maximises what the Tiingo Power plan gives us for those,
and closes the two backtest gaps that turned out not to be gaps at all.

**Ground rules for every phase (non-negotiable):**

1. **Verify before building.** Each phase doc lists its assumptions with file:line
   citations, plus the assumptions that could NOT be pre-verified and must be checked
   first during implementation (marked ⚠ VERIFY). If a ⚠ check fails, stop and
   re-plan — don't code around it.
2. **Unit tests ship with the change**, not after. Each phase doc lists the specific
   tests required. A phase is not done until they pass alongside the full suite.
3. **Parity over invention.** Where a phase ports a live calculation into the
   backtester, the backtest implementation must produce *identical* outputs to the
   live service for identical inputs — proven by shared-fixture parity tests, not by
   eyeballing.
4. **No lookahead.** Any historic computation may only use bars up to and including
   the scoring day. Every phase that touches the backtester includes an explicit
   lookahead test.
5. **Fail-open on live-data checks.** New live-time gates (intraday confirmation
   etc.) must degrade to today's behaviour when the data source is down — a Tiingo
   outage must never silently halt trading logic that worked yesterday.

## Verified facts this plan is built on

| Fact | Source |
|---|---|
| Tiingo Power: 10,000 req/hr, 100,000 req/day, ~108k unique symbols/mo, 40GB/mo | tiingo.com/about/pricing (checked 10 Jul 2026) |
| Tiingo News API included in Power; ticker-tagged; **only 3 months queryable history**; bulk history institutional-only | tiingo.com/documentation/news, /about/pricing |
| IEX intraday bars included (e.g. `resampleFreq=5min`, OHLC + optional volume) | tiingo.com/documentation/iex |
| Tiingo fundamentals is a **paid add-on**, not included in Power | tiingo.com/about/pricing |
| Live Tiingo pacing is hardcoded to the FREE tier: `new RateLimiter(50, TimeSpan.FromHours(1))` | `SwingTrader.Functions/Program.cs:80` |
| Research runs at 4:00 ET *because of* that cap ("75-symbol universe takes ~90 minutes") | `SwingTrader.Functions/SchedulerFunction.cs:42-48` |
| Relative strength = stock 5d return vs sector-ETF 5d return (adjClose), piecewise-linear score | `SwingTrader.Infrastructure/Market/RelativeStrengthService.cs` |
| Sector→ETF map is **hardcoded, 22 symbols → 6 ETFs; everything else falls back to SPY** | `SwingTrader.Infrastructure/Market/SectorEtfMap.cs` |
| Price level = 2-candle swing highs/lows over 120d, 1.5% clustering, 2% proximity, 1.3× breakout volume; fixed scores 1.0/0.85/0.15/0.6/0.5 | `SwingTrader.Infrastructure/Market/PriceLevelService.cs`, `PriceLevelConfig.cs` |
| HistoricalCandles already holds SPY + full S&P 1500 daily bars (5y window) | `CandleSyncService.cs` (`HistoryYears = 5`) |
| Sentiment today: Finnhub company news → Claude JSON score; persisted per signal (`SentimentScore`, `NewsSummary`, `SentimentComponentScore`) | `ResearchPipeline.FetchAndScoreSentimentAsync` (~line 265) |
| `ITiingoClient` has exactly one endpoint today (`/tiingo/daily/{ticker}/prices`) | `SwingTrader.Infrastructure/HttpClients/ITiingoClient.cs` |
| Execution refreshes a Finnhub quote right before placing, re-derives stop/target from it | `ExecutionService.cs` (~lines 260-276) |

## The correction that reshaped this plan

Of the four components the backtester holds at neutral 0.5, only **two** are truly
unreconstructable (Sentiment, Fundamental momentum — no archived news/AI reads).
**Relative strength** needs only ~7 ETF tickers' daily bars added to the candle sync,
and **Price level** needs *nothing* — its inputs are already in the backtest dataset;
it simply was never implemented in `HistoricBacktester`. Phase 1 takes the backtester
from 4/8 to 6/8 live components at near-zero data cost, which directly improves the
Optimizer/A-B tools' ability to find real signal.

## Phases (dependency-ordered; 1 and 2 are independent of each other)

| Phase | Doc | What | Cost |
|---|---|---|---|
| 1 | `01_Phase1_Backtest_RS_PriceLevel.md` | ETF bars into candle sync; RS + PriceLevel implemented in `HistoricBacktester` with parity tests; Lab dial locks reduced 4→2 | Free (data already in plan) |
| 2 | `02_Phase2_Research_Repacing_Midday.md` | Repace Tiingo limiter for Power; move Research later; optional midday rescore so Execution buys on fresh scores | Free |
| 3 | `03_Phase3_Intraday_Entry_Confirmation.md` | IEX 5-min bars; gap/volume sanity gate at order placement | Free (included in Power) |
| 4 | `04_Phase4_TiingoNews_Sentiment_Archive.md` | Tiingo News as second sentiment source; per-symbol daily sentiment archive (start the proprietary-history clock) | Free (included in Power) |

**Explicitly out of scope:** Tiingo fundamentals add-on (paid, lowest expected payoff
— Finnhub covers live needs); any change to the "technicals as gate, current-time as
ranking" strategy architecture (a strategy change, not a data change — revisit only
once Phase 1 backtests and the live demo record give evidence).

## Definition of done, per phase

- All ⚠ VERIFY items resolved and recorded in the phase doc (edit the doc).
- Listed unit tests written and green; full suite green; Angular build green where UI touched.
- Behavioural acceptance criteria in the phase doc demonstrated.
- Committed with the phase doc updated to "Shipped" status at top.
