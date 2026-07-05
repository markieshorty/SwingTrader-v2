# SwingTrader — Watchlist Agent Spec
# Dynamic Universe via Finnhub Index Constituents

## Context

The legacy SwingTrader v1 used a hardcoded
230-symbol curated universe stored in config.
This was a maintenance burden — symbols go
stale, new opportunities are missed, and
the list reflected what seemed interesting
at build time rather than what the market
is actually doing.

SwingTrader v2 replaces this with a dynamic
universe pulled from live index constituents.
The universe is always current, requires zero
maintenance, and automatically captures index
rebalancing events (new additions often have
strong momentum).

---

## Architecture

```
Sunday 8pm ET — WatchlistAgent runs:

Step 1 — Fetch dynamic universe
  GET /index/constituents?symbol=^GSPC
    → S&P 500 (~503 symbols)
  GET /index/constituents?symbol=^NDX
    → Nasdaq 100 (~101 symbols)
  Merge and deduplicate
  Universe: ~550 unique symbols
  Cache 7 days — index rebalances rarely

Step 2 — Screen the universe
  Fetch quotes for all ~550 symbols
  Rate limited: 60 calls/min (free tier)
  Duration: ~10 minutes (acceptable for
    a Sunday evening background job)
  Apply hard filters:
    Price >= $15 AND <= $500
    Volume >= 500,000 daily average
    Abs(ChangePercent) >= 1.0%
      AND <= 15.0%
  Survivors: typically 60-100 symbols

Step 3 — Top movers (if enabled)
  GET /stock/market-gainers
  GET /stock/market-losers
  GET /stock/market-most-active
  Apply same hard filters
  Exclude dot/dash symbols (BRK.B, BF-B)
    → non-standard share classes
  Merge with Step 2 pool
  Deduplicate
  Flag merged entries as IsTopMover=true
  Provides discovery outside S&P 500/NDX

Step 4 — Order candidates for Claude
  Sort by Abs(ChangePercent) descending
  Apply 1.2x ordering boost to IsTopMover
  Take top 80 (MaxCandidatesForClaude)

Step 5 — Claude selects 25
  Send candidate table to Claude
  Include market context (SPY direction,
    VIX level, current regime)
  Claude selects 25 with sector diversity
  Returns symbol + reasoning per pick

Step 6 — Diff and update
  Compare to current active watchlist
  Record additions and removals
  Update WatchlistItems in DB
  Store WatchlistHistory with Claude's
    reasoning for each change
```

---

## New Service: IMarketUniverseService

```csharp
// SwingTrader.Infrastructure/Market/
//   IMarketUniverseService.cs

public interface IMarketUniverseService
{
  /// <summary>
  /// Returns the current screening universe:
  /// deduplicated union of configured index
  /// constituents. Cached for 7 days.
  /// </summary>
  Task<List<string>> GetUniverseAsync(
    CancellationToken ct);
}

// SwingTrader.Infrastructure/Market/
//   MarketUniverseService.cs

public class MarketUniverseService
  : IMarketUniverseService
{
  private readonly IFinnhubClient _finnhub;
  private readonly IMemoryCache _cache;
  private readonly MarketUniverseConfig _config;
  private readonly ILogger<MarketUniverseService>
    _logger;

  private const string CacheKey =
    "market_universe";

  public async Task<List<string>>
    GetUniverseAsync(CancellationToken ct)
  {
    if (_cache.TryGetValue(
        CacheKey, out List<string>? cached)
        && cached != null)
    {
      _logger.LogDebug(
        "Returning cached universe " +
        "({Count} symbols)", cached.Count);
      return cached;
    }

    var symbols = new HashSet<string>(
      StringComparer.OrdinalIgnoreCase);

    foreach (var index in _config.IndexSymbols)
    {
      try
      {
        var constituents = await _finnhub
          .GetIndexConstituentsAsync(index, ct);

        foreach (var s in constituents
          .Where(s => IsValidSymbol(s)))
        {
          symbols.Add(s.ToUpper());
        }

        _logger.LogInformation(
          "Fetched {Count} constituents " +
          "from {Index}", constituents.Count,
          index);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "Failed to fetch constituents " +
          "for {Index} — skipping", index);
        // Continue with other indices
        // Never fail entirely on one index
      }
    }

    if (symbols.Count == 0)
    {
      _logger.LogError(
        "Universe fetch returned zero " +
        "symbols — all index calls failed");
      // Return empty — caller handles this
      return new List<string>();
    }

    var result = symbols.ToList();

    _cache.Set(CacheKey, result,
      TimeSpan.FromDays(
        _config.UniverseCacheDays));

    _logger.LogInformation(
      "Universe built: {Count} symbols " +
      "from {Indices} indices",
      result.Count,
      string.Join(", ", _config.IndexSymbols));

    return result;
  }

  private static bool IsValidSymbol(
    string symbol)
  {
    // Exclude non-standard share classes
    // and non-US exchange symbols
    return !symbol.Contains('.')
      && !symbol.Contains('-')
      && symbol.Length >= 1
      && symbol.Length <= 5
      && symbol.All(char.IsLetter);
  }
}
```

---

## New Finnhub Client Method

```csharp
// Add to IFinnhubClient:

[Get("/index/constituents")]
Task<IndexConstituentsResponse>
  GetIndexConstituentsAsync(
    [AliasAs("symbol")] string indexSymbol,
    CancellationToken ct = default);

// DTO:
public class IndexConstituentsResponse
{
  [JsonPropertyName("constituents")]
  public List<string> Constituents
    { get; set; } = new();

  [JsonPropertyName("symbol")]
  public string Symbol { get; set; }
    = string.Empty;
}
```

Available index symbols on Finnhub free tier:
  ^GSPC  — S&P 500 (~503 symbols)
  ^NDX   — Nasdaq 100 (~101 symbols)
  ^DJI   — Dow Jones 30
  ^RUT   — Russell 2000 (wider universe,
            higher noise, lower liquidity)

Recommend: ^GSPC + ^NDX for most users.
^RUT optional if wanting mid-cap exposure.

---

## Configuration

```json
"Watchlist": {
  "IndexSymbols": ["^GSPC", "^NDX"],
  "UniverseCacheDays": 7,
  "MaxCandidatesForClaude": 80,
  "TopMoversEnabled": false,
  "TopMoverOrderBoost": 1.2,
  "HardFilters": {
    "MinPrice": 15.0,
    "MaxPrice": 500.0,
    "MinDailyVolume": 500000,
    "MinChangePercent": 1.0,
    "MaxChangePercent": 15.0
  }
}
```

Strongly typed config class:

```csharp
public class WatchlistConfig
{
  public List<string> IndexSymbols
    { get; set; } = new() { "^GSPC", "^NDX" };
  public int UniverseCacheDays
    { get; set; } = 7;
  public int MaxCandidatesForClaude
    { get; set; } = 80;
  public bool TopMoversEnabled
    { get; set; } = false;
  public decimal TopMoverOrderBoost
    { get; set; } = 1.2m;
  public WatchlistHardFilters HardFilters
    { get; set; } = new();
}

public class WatchlistHardFilters
{
  public decimal MinPrice { get; set; } = 15m;
  public decimal MaxPrice { get; set; } = 500m;
  public long MinDailyVolume { get; set; }
    = 500_000;
  public decimal MinChangePercent
    { get; set; } = 1.0m;
  public decimal MaxChangePercent
    { get; set; } = 15.0m;
}
```

Register in Program.cs and Functions startup:

```csharp
services.Configure<WatchlistConfig>(
  configuration.GetSection("Watchlist"));
services.AddScoped<IMarketUniverseService,
  MarketUniverseService>();
```

---

## WatchlistAgent Integration

The existing WatchlistAgent (reference
legacy SwingTrader v1 WatchlistAgent.cs)
replaces its hardcoded symbol list call
with IMarketUniverseService:

```csharp
// Before (v1):
var universe = _config.CuratedSymbols;
  // List<string> from appsettings

// After (v2):
var universe = await _universeService
  .GetUniverseAsync(ct);

if (universe.Count == 0)
{
  _logger.LogError(
    "Universe fetch failed — " +
    "watchlist refresh aborted. " +
    "Check Finnhub index endpoints.");
  return;
}
```

Everything after this point is identical
to the v1 implementation:
  Screen universe via Finnhub quotes
  Apply hard filters
  Merge top movers (if enabled)
  Order by change % with mover boost
  Send top 80 to Claude
  Claude selects 25
  Diff and update DB

---

## Rate Limiting During Screening

Screening 550 symbols at Finnhub's
60 calls/minute free tier limit:

```csharp
// Use existing RateLimiter from v1
// (already implemented in infrastructure)
// Each quote call waits for token

// Approximate timing:
// 550 symbols / 60 per minute = 9.2 minutes
// Acceptable for a Sunday evening job
// WatchlistFunction runs at 1am UTC Sunday
// (8pm ET Sunday — market closed, no urgency)
```

If rate limiting is a concern, Tiingo's
bulk endpoint can screen multiple symbols
faster — worth considering if the 10-minute
window causes issues in practice.

---

## New User Seeding (Phase 10c/10e)

When a new user signs in for the first time,
UserRegistrationMiddleware seeds a default
watchlist. Previously this seeded from a
hardcoded list.

With the dynamic universe, seeding works
differently — we can't pre-populate the
watchlist because the universe changes.

Instead, seed a small starter set of
reliable high-liquidity names that will
always pass the screener filters:

```csharp
// SwingTrader.Agents/Watchlist/
//   WatchlistSeedService.cs

private static readonly List<string>
  StarterSymbols = new()
{
  "AAPL", "MSFT", "NVDA", "GOOGL", "AMZN",
  "META", "TSLA", "AMD", "AVGO", "LLY",
  "V", "JPM", "UNH", "XOM", "COST",
  "NFLX", "CRM", "PYPL", "CAT", "NEE",
  "ABBV", "SNOW", "LRCX", "TSM", "AMGN"
};

public async Task SeedDefaultWatchlistAsync(
  string userId, CancellationToken ct)
{
  var watchlist = await _repo.CreateAsync(
    userId,
    name: "AI Picks",
    type: WatchlistType.AiManaged,
    ct: ct);

  foreach (var symbol in StarterSymbols)
  {
    await _repo.AddSymbolAsync(
      userId, watchlist.Id, symbol, ct);
  }

  // Mark as default and enabled
  await _repo.SetDefaultAsync(
    userId, watchlist.Id, ct);
  await _repo.EnableAsync(
    userId, watchlist.Id, ct);

  _logger.LogInformation(
    "Seeded default watchlist for " +
    "new user {UserId} with {Count} " +
    "starter symbols",
    userId, StarterSymbols.Count);
}
```

The Watchlist Agent will replace these
starter symbols on the first Sunday evening
after sign-up. The starter list just ensures
the system has something to research on day
one if the user signs up mid-week.

---

## Tests

### MarketUniverseServiceTests

```
Test: GetUniverse_FetchesBothIndices
- Mock Finnhub returning 503 for ^GSPC
  and 101 for ^NDX
- Assert result contains symbols from both
- Assert count ≈ 550 (after deduplication)

Test: GetUniverse_Deduplicates
- ^GSPC and ^NDX both contain "AAPL"
  and "MSFT"
- Assert AAPL appears only once in result
- Assert MSFT appears only once

Test: GetUniverse_CachedForConfiguredDays
- Call GetUniverseAsync twice
- Assert Finnhub called only once
  (second call hits cache)

Test: GetUniverse_InvalidSymbolsExcluded
- ^GSPC response includes "BRK.B"
  and "BF-B"
- Assert neither appears in result

Test: GetUniverse_OneFails_OtherSucceeds
- Mock ^GSPC call throws
- Mock ^NDX returns 101 symbols
- Assert result contains NDX symbols
- Assert warning logged for ^GSPC failure
- Assert no exception propagates

Test: GetUniverse_BothFail_ReturnsEmpty
- Both index calls throw
- Assert result is empty list
- Assert error logged
- Assert no exception propagates

Test: GetUniverse_CacheExpiry_RefetchesAfterDays
- Call GetUniverseAsync
- Advance time by UniverseCacheDays + 1
- Call again
- Assert Finnhub called twice

Test: IsValidSymbol_ExcludesDotSymbols
- "BRK.B" → excluded
- "GOOGL" → included

Test: IsValidSymbol_ExcludesDashSymbols
- "BF-B" → excluded
- "META" → included

Test: IsValidSymbol_ExcludesLongSymbols
- "TOOLONG" (7 chars) → excluded
- "NVDA" → included
```

### WatchlistAgentTests (updated)

```
Test: WatchlistAgent_UsesUniverseService
- Mock IMarketUniverseService returning
  known list of 50 symbols
- Run WatchlistAgent
- Assert screener called with those 50 symbols
- Assert NOT called with any hardcoded list

Test: WatchlistAgent_AbortOnEmptyUniverse
- Mock IMarketUniverseService returning
  empty list (all index calls failed)
- Run WatchlistAgent
- Assert error logged
- Assert watchlist NOT updated in DB
- Assert no Claude call made

Test: WatchlistAgent_ScreensUniverse
- Universe: 100 symbols
- 40 pass hard filters
- Assert Claude receives ≤ 80 candidates
- Assert all passed symbols were in universe

Test: WatchlistAgent_FiltersApplied
- Symbol with price $12 → excluded (< $15)
- Symbol with price $600 → excluded (> $500)
- Symbol with volume 100k → excluded
- Symbol with change 0.5% → excluded
- Symbol with change 20% → excluded
- Valid symbol → included
```

---

## Deliverables

1. dotnet test — all tests green
   (new MarketUniverseService tests pass)

2. Trigger watchlist refresh manually:
   POST /run/watchlist
   Check logs for:
   "Universe built: {n} symbols from
    ^GSPC, ^NDX indices"

3. Check Finnhub calls in Application Insights:
   Should see ~2 constituent calls
   followed by ~550 quote calls
   Total runtime: 8-12 minutes

4. After completion, check DB:
   SELECT Symbol, CompanyName, AddedAt
   FROM WatchlistItems
   WHERE UserId = '{your-id}'
   AND IsActive = 1
   Should show 25 symbols with Claude's
   selection reasoning in WatchlistHistory

5. Verify no hardcoded symbols in codebase:
   Search solution for any List<string>
   containing stock tickers
   Assert only StarterSymbols seed list exists
   No other hardcoded universe

6. Cache verified:
   Trigger watchlist refresh twice
   Second run completes in seconds
   (universe served from cache)
   No duplicate Finnhub index calls

7. Config verified:
   Change IndexSymbols to ["^GSPC"] only
   Restart and trigger refresh
   Assert only S&P 500 constituents used
   Restore to ["^GSPC", "^NDX"]

---

## Notes for Claude Code

Reference legacy repo for:
  WatchlistAgent.cs — full screening and
    Claude selection logic
  TopMoversService.cs — already implemented
    in v1, reuse directly
  WatchlistHistory recording pattern

New in v2 (not in legacy):
  MarketUniverseService — build fresh
  IFinnhubClient.GetIndexConstituentsAsync
    — add new Refit method
  WatchlistConfig — replace
    CuratedSymbols config section
    with IndexSymbols config section

The hardcoded 230-symbol list from v1
does NOT exist in v2. Do not recreate it.
