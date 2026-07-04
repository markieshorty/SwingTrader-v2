# SwingTrader — Phase 10g
# Risk Customisation, Multiple Watchlists, Admin Area

## Context
Phases 10a-10f complete or in progress.
Multi-tenant system with Google Sign-On,
encrypted keys, per-user scheduling,
onboarding, and global refinement.

Phase 10g adds three features:
  1. Per-user risk profile — users adjust
     capital allocation and position sizing
     within enforced safety bounds
  2. Multiple named watchlists — users save
     and multi-select watchlists, Research
     Agent scans the enabled union
  3. Admin area — you manage all users,
     see system health, retry failed jobs

---

## Step 1: Bicep — SQL Upgrade for Multi-User

When serving multiple family members,
upgrade Azure SQL from Basic to Standard S0.
Handles more concurrent connections.

### Update infra/modules/sql.bicep

The sql.bicep module already accepts
sqlTier and sqlCapacity params (from Phase 10a).
No code changes needed to the module.

### Update infra/parameters/prod.bicepparam

```bicep
using '../main.bicep'

param environment = 'prod'
param location = 'uksouth'
param sqlTier = 'S0'
param sqlCapacity = 10
// sqlAdminPassword and adminUserId
// passed as GitHub secrets at deploy time
```

Push this change → deploy-infra.yml
upgrades the database automatically.
Azure SQL scales online — zero downtime.

Cost change: Basic £4/month → S0 £12/month.

---

## Step 2: Per-User Risk Profile

### Philosophy

Current CapitalRules is a static class
with hardcoded constants. These become
per-user configurable defaults within
enforced safety bounds.

Safety bounds are non-negotiable and
enforced at both API and service layer.
A misconfigured profile cannot allow
a single bad day to wipe everything.

### CapitalRules Static Class (Keep)

CapitalRules now holds ALLOWED RANGES
and DEFAULT VALUES, not actual values
used at runtime:

```csharp
// SwingTrader.Core/Capital/CapitalRules.cs
public static class CapitalRules
{
  // Defaults (used when seeding new users)
  public const decimal DefaultLockedPct = 0.70m;
  public const decimal DefaultMaxPositionPct = 0.20m;
  public const int DefaultMaxPositions = 3;
  public const decimal DefaultCircuitBreaker = 0.05m;
  public const decimal DefaultBuyThreshold = 7.0m;
  public const decimal DefaultWatchThreshold = 5.0m;
  public const int DefaultTier1MinTrades = 30;
  public const decimal DefaultTier1MinWinRate = 0.55m;
  public const int DefaultTier2MinTrades = 60;
  public const decimal DefaultTier2MinWinRate = 0.58m;

  // Allowed ranges (hard safety bounds)
  public const decimal MinLockedPct = 0.50m;
  public const decimal MaxLockedPct = 0.90m;
  public const decimal MinMaxPositionPct = 0.05m;
  public const decimal MaxMaxPositionPct = 0.33m;
  public const int MinMaxPositions = 1;
  public const int MaxMaxPositions = 10;
  public const decimal MinCircuitBreaker = 0.02m;
  public const decimal MaxCircuitBreaker = 0.15m;
  public const decimal MinBuyThreshold = 5.0m;
  public const decimal MaxBuyThreshold = 9.5m;
  public const int MinTier1Trades = 20;
  public const int MaxTier1Trades = 100;
  public const int MaxTier2Trades = 200;
}
```

### UserRiskProfile Entity

```csharp
// SwingTrader.Core/Entities/UserRiskProfile.cs
public class UserRiskProfile : BaseEntity
{
  public string UserId { get; set; }

  public decimal LockedCapitalPct
    { get; set; } = 0.70m;
    // Range: 0.50 to 0.90

  public decimal MaxPositionPctOfActive
    { get; set; } = 0.20m;
    // Range: 0.05 to 0.33

  public int MaxOpenPositions
    { get; set; } = 3;
    // Range: 1 to 10

  public decimal DailyLossCircuitBreakerPct
    { get; set; } = 0.05m;
    // Range: 0.02 to 0.15

  public decimal BuyThreshold
    { get; set; } = 7.0m;
    // Range: 5.0 to 9.5

  public decimal WatchThreshold
    { get; set; } = 5.0m;
    // Range: 3.0 to BuyThreshold - 0.1

  public int Tier1UnlockMinTrades
    { get; set; } = 30;
    // Range: 20 to 100

  public decimal Tier1UnlockMinWinRate
    { get; set; } = 0.55m;
    // Range: 0.50 to 0.80

  public int Tier2UnlockMinTrades
    { get; set; } = 60;
    // Range: Tier1+1 to 200

  public decimal Tier2UnlockMinWinRate
    { get; set; } = 0.58m;
    // Range: Tier1Rate+0.01 to 0.85

  public decimal DefaultStopLossPct
    { get; set; } = 0.05m;
    // Range: 0.02 to 0.15

  public string RiskLabel => LockedCapitalPct switch
  {
    >= 0.80m => "Very Conservative",
    >= 0.70m => "Conservative",
    _ when MaxPositionPctOfActive >= 0.25m
      => "Moderate-Aggressive",
    _ => "Moderate"
  };

  public void Validate()
  {
    if (LockedCapitalPct < 0.50m ||
        LockedCapitalPct > 0.90m)
      throw new ValidationException(
        "Locked capital must be 50%-90%");

    if (MaxPositionPctOfActive < 0.05m ||
        MaxPositionPctOfActive > 0.33m)
      throw new ValidationException(
        "Max position must be 5%-33% of active");

    if (MaxOpenPositions < 1 ||
        MaxOpenPositions > 10)
      throw new ValidationException(
        "Max positions must be 1-10");

    if (DailyLossCircuitBreakerPct < 0.02m ||
        DailyLossCircuitBreakerPct > 0.15m)
      throw new ValidationException(
        "Circuit breaker must be 2%-15%");

    if (BuyThreshold < 5.0m ||
        BuyThreshold > 9.5m)
      throw new ValidationException(
        "Buy threshold must be 5.0-9.5");

    if (WatchThreshold >= BuyThreshold)
      throw new ValidationException(
        "Watch threshold must be below " +
        "buy threshold");

    if (Tier2UnlockMinTrades <=
        Tier1UnlockMinTrades)
      throw new ValidationException(
        "Tier 2 min trades must exceed " +
        "Tier 1 min trades");

    // Active capital sanity check
    var activePct = 1.0m - LockedCapitalPct;
    if (MaxPositionPctOfActive > activePct)
      throw new ValidationException(
        "Max position exceeds available " +
        "active capital");
  }
}
```

### Replace CapitalRules References

Every service that reads from the static
CapitalRules class must be updated to
read from UserRiskProfile instead.

Services to update:
  PositionSizingService
  PortfolioCircuitBreakerService
  TierEvaluationService
  ResearchPipeline (BuyThreshold,
    WatchThreshold)
  ExecutionService
  MonitorService

Pattern for each service:

```csharp
// Before:
var maxPosition = CapitalRules.DefaultMaxPositionPct;

// After:
var profile = await _riskProfileRepo
  .GetAsync(_userContext.UserId, ct);
var maxPosition = profile.MaxPositionPctOfActive;
```

IUserRiskProfileRepository:

```csharp
Task<UserRiskProfile> GetAsync(
  string userId, CancellationToken ct);
  // Returns default profile if none exists

Task UpdateAsync(
  UserRiskProfile profile,
  CancellationToken ct);
  // Calls profile.Validate() before saving

Task<UserRiskProfile> ResetToDefaultsAsync(
  string userId, CancellationToken ct);
```

### Risk Profile API Endpoints

```csharp
app.MapGroup("/api/risk-profile")
  .RequireAuthorization()
  .MapGet("/", GetRiskProfile)
  .MapPut("/", UpdateRiskProfile)
  .MapPost("/reset", ResetRiskProfile);
```

GET /api/risk-profile returns:

```json
{
  "lockedCapitalPct": 0.70,
  "maxPositionPctOfActive": 0.20,
  "maxOpenPositions": 3,
  "dailyLossCircuitBreakerPct": 0.05,
  "buyThreshold": 7.0,
  "watchThreshold": 5.0,
  "tier1UnlockMinTrades": 30,
  "tier1UnlockMinWinRate": 0.55,
  "riskLabel": "Conservative",
  "capitalBreakdown": {
    "totalCapital": 1000.00,
    "lockedCapital": 700.00,
    "activeCapital": 100.00,
    "maxPerTrade": 20.00,
    "currentTier": 1
  },
  "allowedRanges": {
    "lockedCapitalPct": { "min": 0.50, "max": 0.90 },
    "maxPositionPctOfActive": { "min": 0.05, "max": 0.33 },
    "maxOpenPositions": { "min": 1, "max": 10 },
    "dailyLossCircuitBreakerPct": { "min": 0.02, "max": 0.15 },
    "buyThreshold": { "min": 5.0, "max": 9.5 }
  }
}
```

### Angular: Risk Management Settings Tab

New tab in Settings: [Risk Management]

Section 1 — Capital Allocation:
  Locked Capital slider (50%-90%)
  Live preview: "At £1,000: Protected £700,
    Available to trade £300"

Section 2 — Position Sizing:
  Max position size slider (5%-33%)
  Max open positions stepper (1-10)
  Circuit breaker slider (2%-15%)
  Live examples with £ amounts

Section 3 — Signal Thresholds:
  Buy threshold slider (5.0-9.5)
  Watch threshold slider (3.0 to buy-0.1)
  Live count: "Today: {n} signals qualify"

Section 4 — Advanced (collapsible):
  Tier 1 → 2 unlock criteria
  Tier 2 → 3 unlock criteria

Risk label badge (live, updates as sliders move):
  Very Conservative / Conservative /
  Moderate / Moderate-Aggressive

[Save Changes] [Reset to Defaults]

Unsaved changes indicator showing diff:
  "Locked: 70% → 75%"

Validation errors inline:
  Save button disabled until all valid

### Migration: AddUserRiskProfile

```bash
dotnet ef migrations add AddUserRiskProfile \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

---

## Step 3: Multiple Watchlists

### Philosophy

Users create multiple named watchlists.
Each has a type: Manual or AiManaged.
Multiple can be enabled simultaneously.
Research Agent scans the deduplicated
union of all enabled watchlists.

Caps:
  50 symbols per watchlist
  3 watchlists enabled simultaneously
  = 150 symbols maximum (Finnhub rate limit)

### Entities

```csharp
// SwingTrader.Core/Entities/Watchlist.cs
public class Watchlist : BaseEntity
{
  public string UserId { get; set; }
  public string Name { get; set; }
  public WatchlistType Type { get; set; }
  public bool IsEnabled { get; set; }
  public bool IsDefault { get; set; }
  public string? Description { get; set; }
  public List<WatchlistItem> Items
    { get; set; } = new();
}

public enum WatchlistType
{
  AiManaged,
    // Watchlist Agent refreshes weekly
  Manual,
    // User fully controls
  Mixed
    // AI adds but never removes
    // user-added symbols
}

// WatchlistItem adds WatchlistId FK:
public class WatchlistItem : BaseEntity
{
  public string UserId { get; set; }
  public int WatchlistId { get; set; }
  public Watchlist Watchlist { get; set; }
  public string Symbol { get; set; }
  public string CompanyName { get; set; }
  public string? Sector { get; set; }
  public bool IsActive { get; set; } = true;
  public string? Notes { get; set; }
}
```

### IWatchlistRepository (Extended)

```csharp
// Add to existing interface:
Task<List<Watchlist>> GetAllAsync(
  string userId, CancellationToken ct);

Task<List<WatchlistItem>>
  GetAllEnabledSymbolsAsync(
    string userId, CancellationToken ct);
    // Deduplicated union of enabled watchlists

Task<Watchlist> CreateAsync(
  string userId, string name,
  WatchlistType type,
  CancellationToken ct);

Task EnableAsync(
  string userId, int watchlistId,
  CancellationToken ct);
  // Validates: max 3 enabled

Task DisableAsync(
  string userId, int watchlistId,
  CancellationToken ct);

Task AddSymbolAsync(
  string userId, int watchlistId,
  string symbol, CancellationToken ct);
  // Validates: symbol exists on Finnhub
  // Validates: max 50 per watchlist

Task RemoveSymbolAsync(
  string userId, int watchlistId,
  string symbol, CancellationToken ct);

Task DeleteAsync(
  string userId, int watchlistId,
  CancellationToken ct);
  // Fails if IsDefault = true

Task SetDefaultAsync(
  string userId, int watchlistId,
  CancellationToken ct);
```

### Symbol Validation

When adding a symbol, validate it exists:

```csharp
// In AddSymbolAsync implementation:
var quote = await _finnhubClient
  .GetQuoteAsync(symbol, ct);
if (quote.C == 0)
  throw new ValidationException(
    $"Symbol '{symbol}' not found on Finnhub");
```

### Research Pipeline Change

In ResearchConsumerFunction, replace:

```csharp
// Before:
var symbols = await _watchlistRepo
  .GetActiveAsync(ct);

// After:
var symbols = await _watchlistRepo
  .GetAllEnabledSymbolsAsync(userId, ct);
```

Deduplication happens in the repository.
If symbol appears in multiple enabled
watchlists, it's researched once.

### Watchlist Agent Change

The Watchlist Agent only manages watchlists
where Type == AiManaged AND IsDefault == true.
All other watchlists are untouched.

### Watchlist API Endpoints

```csharp
app.MapGroup("/api/watchlists")
  .RequireAuthorization()
  .MapGet("/", GetWatchlists)
  .MapPost("/", CreateWatchlist)
  .MapGet("/{id}/symbols", GetSymbols)
  .MapPost("/{id}/symbols", AddSymbol)
  .MapDelete("/{id}/symbols/{symbol}",
    RemoveSymbol)
  .MapPost("/{id}/enable", EnableWatchlist)
  .MapPost("/{id}/disable", DisableWatchlist)
  .MapPost("/{id}/set-default", SetDefault)
  .MapPut("/{id}", UpdateWatchlist)
  .MapDelete("/{id}", DeleteWatchlist)
  .MapGet("/enabled-symbols",
    GetEnabledSymbols);
```

### Angular: Watchlist Page

New page: /watchlists
Add to nav: Watchlist icon

Layout: cards per watchlist

Each card shows:
  Enabled indicator (●/○)
  Name and type badge (AI/Manual/Mixed)
  Symbol count
  Default badge if applicable
  [View symbols] [Enable/Disable] [⋮ More]

⋮ More menu:
  Set as default, Rename, Duplicate, Delete

Enable guard:
  If enabling 4th → dialog prompts user to
  disable one of the 3 currently enabled

Symbol management:
  Full-screen Material Dialog
  Search/add bar at top
  AG Grid with: Symbol | Company | Sector |
    Notes | [Remove]
  "12 / 50 symbols" progress indicator
  [Import from CSV] button

New Watchlist Dialog:
  Name field
  Type selection with descriptions
  Optional description

### Migration: AddMultipleWatchlists

```bash
dotnet ef migrations add AddMultipleWatchlists \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

Migration should:
  Create Watchlist table
  Add WatchlistId FK to WatchlistItem
  Migrate existing WatchlistItems:
    Create one Watchlist record per user
    (Name="AI Picks", Type=AiManaged,
     IsEnabled=true, IsDefault=true)
    Set WatchlistId on all existing items

---

## Step 4: Admin Area

### Access Control

Admin access by UserId match only.
Your B2C object ID is in Key Vault
as Admin--UserId (set in Phase 10c).

AdminHandler reads from config:
  Admin:UserId → from Key Vault

Applied as an authorization policy
on all /api/admin/* endpoints.

### Admin Entities

```csharp
// SwingTrader.Core/Entities/AdminActionLog.cs
// Append-only — never modify or delete
public class AdminActionLog : BaseEntity
{
  public string AdminUserId { get; set; }
  public string TargetUserId { get; set; }
  public string Action { get; set; }
    // "Suspend", "Unsuspend", "ForceDemo",
    // "ResetOnboarding", "DeleteUser",
    // "SendMessage", "RetryJob"
  public string? Details { get; set; }
  public DateTime PerformedAt { get; set; }
}
```

AppUser additions (add these fields):

```csharp
// To existing AppUser entity:
public bool IsSuspended { get; set; } = false;
public DateTime? SuspendedAt { get; set; }
public string? SuspendReason { get; set; }
```

### Admin API Endpoints

```csharp
app.MapGroup("/api/admin")
  .RequireAuthorization("Admin")
  .MapGet("/me", () => Results.Ok(
    new { isAdmin = true }))
  .MapGet("/stats", GetStats)
  .MapGet("/users", GetUsers)
  .MapGet("/users/{userId}", GetUserDetail)
  .MapPost("/users/{userId}/suspend",
    SuspendUser)
  .MapPost("/users/{userId}/unsuspend",
    UnsuspendUser)
  .MapPost("/users/{userId}/reset-onboarding",
    ResetOnboarding)
  .MapPost("/users/{userId}/force-demo",
    ForceDemo)
  .MapPost("/users/{userId}/send-message",
    SendMessage)
  .MapDelete("/users/{userId}", DeleteUser)
  .MapGet("/jobs/failures", GetJobFailures)
  .MapPost("/jobs/retry", RetryJob)
  .MapGet("/logs", GetAdminLogs);
```

Admin DTOs:

```csharp
public record AdminUserSummary(
  string UserId,
  string Email,
  string DisplayName,
  DateTime FirstLoginAt,
  DateTime LastLoginAt,
  bool IsOnboarded,
  bool IsSuspended,
  string T212Mode,
  int TotalTrades,
  decimal? WinRate,
  Dictionary<string, KeyStatus> KeyStatuses,
  int FailedJobsLast48h,
  string RiskLabel,
  int EnabledWatchlistCount,
  DataMaturityLevel DataMaturity);

public record AdminStats(
  int TotalUsers,
  int ActiveUsersLast7Days,
  int TotalTradesAllTime,
  decimal AverageWinRateAllUsers,
  int UsersInDemoMode,
  int UsersInLiveMode,
  int UsersNotOnboarded,
  int TotalJobFailuresLast24h);
```

### Suspension Handling

SchedulerFunction skips suspended users:

```csharp
var users = await GetActiveUsersAsync(ct);
// GetActiveUsersAsync filters:
// WHERE IsActive = 1 AND IsSuspended = 0
```

API middleware blocks suspended users:

```csharp
public class SuspensionMiddleware
{
  public async Task InvokeAsync(
    HttpContext context,
    IUserRepository users,
    IUserContext userContext)
  {
    if (context.User.Identity?.IsAuthenticated
        == true &&
        !context.Request.Path
          .StartsWithSegments("/health"))
    {
      var user = await users
        .GetAsync(userContext.UserId);
      if (user?.IsSuspended == true)
      {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(
          new { error = "Account suspended",
                message = "Contact support" });
        return;
      }
    }
    await _next(context);
  }
}
```

### Admin Actions Log All Operations

Every admin action calls:

```csharp
await _adminLogRepo.LogAsync(new AdminActionLog
{
  AdminUserId = _userContext.UserId,
  TargetUserId = targetUserId,
  Action = "Suspend",
  Details = $"Reason: {reason}",
  PerformedAt = DateTime.UtcNow
});
```

### Admin Daily Summary Email

New AdminSummaryFunction:

```csharp
[Function("AdminSummary")]
public async Task Run(
  [TimerTrigger("0 0 8 * * *")] // 8am UTC daily
    TimerInfo timer,
  CancellationToken ct)
{
  var stats = await BuildStatsAsync(ct);

  var subject = stats.TotalJobFailuresLast24h > 5
    ? $"⚠️ SwingTrader Admin — {stats.TotalJobFailuresLast24h} job failures"
    : "SwingTrader Admin — Daily Summary";

  await _emailService.SendAdminSummaryAsync(
    _adminEmail, subject, stats, ct);
}
```

### Angular Admin Area

Route: /admin (guarded by adminGuard)
Add to sidenav (admin only — check isAdmin()):

```typescript
@if (auth.isAdmin()) {
  <mat-divider />
  <a mat-list-item routerLink="/admin"
    routerLinkActive="active">
    <mat-icon>admin_panel_settings</mat-icon>
    Admin
  </a>
}
```

Four tabs: [Overview] [Users] [Jobs] [Logs]

#### Overview Tab
  Stat cards: Total Users, Active (7d),
    Total Trades, Avg Win Rate
  Line chart: user growth
  Bar chart: trades per day (30 days)
  Donut: Demo vs Live split
  Recent failures widget with [Retry] buttons

#### Users Tab

  Search + filter chips:
  [All] [Demo] [Live] [Suspended]
  [Not Onboarded]

  AG Grid (server-side pagination):
  Name | Email | Joined | Last Login |
  Mode | Trades | Win% | Status | Keys | [Actions]

  Keys column: coloured dots per provider
    (green=valid, red=invalid, grey=not set)

  [Actions] → [View] button + [⋮] menu:
    Suspend/Unsuspend
    Reset Onboarding
    Force Demo
    Send Message
    Delete Account

  UserDetailDrawer (right-side, slides in):
    Tabs: Overview | Risk | Watchlists |
      Jobs | Trades | Readiness
    Action buttons at bottom:
      [Suspend] [Reset Onboarding]
      [Force Demo] [Send Message]
      [Delete Account] (red, confirmation)

#### Jobs Tab
  Failed jobs across all users
  Last 48h default, [24h/48h/7d] filter
  User | Job Type | Date | Error | [Retry]
  [Retry All Failed Today] bulk button

#### Logs Tab
  AdminActionLog entries (read-only)
  Admin | Target | Action | Details | Time

### Admin Guard (Angular)

```typescript
// src/app/core/guards/admin.guard.ts
export const adminGuard: CanActivateFn =
  (route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);

    if (auth.isAdmin()) return true;

    return router.createUrlTree(['/dashboard']);
  };
```

Apply to /admin route:

```typescript
{
  path: 'admin',
  canActivate: [authGuard, adminGuard],
  loadComponent: () =>
    import('./features/admin/admin.component')
      .then(m => m.AdminComponent)
}
```

### Migration: AddAdminAndRiskTables

```bash
dotnet ef migrations add AddAdminAndRiskTables \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

Creates:
  AdminActionLog table
  UserRiskProfile table
  Adds IsSuspended, SuspendedAt,
    SuspendReason to AppUser

---

## Step 5: DI Registration

```csharp
// Risk profile
services.AddScoped<IUserRiskProfileRepository,
  UserRiskProfileRepository>();

// Watchlists (already registered — add
// new methods to existing implementation)

// Admin
services.AddScoped<IAdminLogRepository,
  AdminLogRepository>();
services.AddSingleton<IAuthorizationHandler,
  AdminHandler>();

// Suspension middleware
app.UseMiddleware<SuspensionMiddleware>();
// Register AFTER authentication middleware
```

---

## Tests

### Risk Profile Tests

```
Test: DefaultProfile_MatchesCapitalRulesDefaults
  New profile created
  Assert all values match CapitalRules
  default constants

Test: Validate_LockedBelow50_Throws
  LockedCapitalPct = 0.40
  Assert ValidationException

Test: Validate_MaxPositionExceedsActive_Throws
  LockedCapitalPct = 0.80 (20% active)
  MaxPositionPctOfActive = 0.25
  Assert ValidationException

Test: Validate_WatchAboveBuy_Throws
  BuyThreshold = 7.0, WatchThreshold = 7.5
  Assert ValidationException

Test: Validate_Tier2LessThanTier1_Throws
  Tier1UnlockMinTrades = 50
  Tier2UnlockMinTrades = 40
  Assert ValidationException

Test: PositionSizing_UsesUserProfile
  Profile: MaxPositionPctOfActive = 0.10
  Assert max trade uses 0.10 not 0.20 default

Test: CircuitBreaker_UsesUserProfile
  Profile: DailyLossCircuitBreakerPct = 0.03
  Portfolio drops 3.5% in a day
  Assert circuit breaker fires

Test: BuyThreshold_FromUserProfile
  Profile: BuyThreshold = 8.0
  Signal conviction = 7.5
  Assert recommendation = Watch (not Buy)
```

### Watchlist Tests

```
Test: AddSymbol_ValidatesOnFinnhub
  Add "INVALID_SYM_XYZ"
  Mock Finnhub returning empty quote
  Assert ValidationException

Test: AddSymbol_Cap50Enforced
  50 symbols already in watchlist
  Try adding 51st
  Assert 409 Conflict

Test: EnableWatchlist_Cap3Enforced
  3 watchlists already enabled
  Try enabling 4th
  Assert 409 Conflict

Test: GetEnabledSymbols_Deduplicates
  Watchlist A: [NVDA, AAPL, MSFT]
  Watchlist B: [NVDA, TSLA, AMD]
  Both enabled
  Assert result: [NVDA, AAPL, MSFT, TSLA, AMD]
  Count: 5 not 6

Test: AiAgent_OnlyManagesDefaultWatchlist
  2 watchlists: one AI default, one manual
  Watchlist Agent runs
  Assert only AI default modified
  Assert manual watchlist unchanged

Test: DeleteWatchlist_FailsIfDefault
  Attempt to delete IsDefault watchlist
  Assert 409 Conflict
```

### Admin Tests

```
Test: AdminEndpoint_NonAdmin_Returns403
  Regular user calls GET /api/admin/stats
  Assert 403

Test: SuspendUser_PreventsJobEnqueue
  Admin suspends user A
  Scheduler runs
  Assert no jobs enqueued for user A
  Assert other users' jobs still enqueued

Test: SuspendedUser_API_Returns403
  User A is suspended
  User A calls GET /api/portfolio
  Assert 403 with suspension message

Test: ForceDemo_UpdatesT212Mode
  User in Live mode
  Admin calls force-demo
  Assert T212Mode = Demo
  Assert AdminActionLog entry created

Test: DeleteUser_RemovesAllData
  User with trades, signals, watchlists
  Admin deletes
  Assert all tables empty for UserId
  Assert AppUser removed

Test: AdminActionLog_AllActionsRecorded
  Perform: suspend, unsuspend, force-demo
  GET /api/admin/logs
  Assert 3 entries in chronological order

Test: RetryJob_ReenqueuesMessage
  Failed job in JobLog
  Admin calls retry
  Assert new message in Service Bus queue

Test: AdminSummaryEmail_SentAt8amUtc
  Mock 8am UTC trigger
  Assert admin receives summary email
  Assert counts accurate
```

---

## Deliverables

### Risk Profile
1. dotnet test — all tests green
2. Settings → Risk Management tab:
   Sliders, steppers render correctly
   Live preview updates in real time
3. Save changes persists:
   GET /api/risk-profile returns new values
4. Validation works:
   Set buy threshold ≤ watch threshold
   Assert save disabled with error shown
5. Research uses user threshold:
   Set buy threshold = 9.0
   Trigger research
   Assert fewer BUY signals than default

### Watchlists
6. Watchlists page shows existing AI Picks
   (migrated from Phase 10a data)
7. Create manual watchlist:
   Add 5 valid symbols
   Add one invalid → rejected with error
8. Enable second watchlist:
   Research scans symbols from both
   Log shows "Scanning X symbols from
   Y enabled watchlists"
9. Enable 3: works
   Enable 4th: dialog to disable one first
10. Symbol count correct per card

### Admin
11. /admin loads for your account
    Shows stats (may be small numbers)
12. /admin for another account:
    Redirects to /dashboard
13. Users tab shows all registered users
14. UserDetailDrawer opens with all tabs
15. Suspend a test account:
    Test account gets 403 on API
    Jobs not enqueued for that account
16. Unsuspend: access restored
17. Admin daily summary email at 8am UTC
18. All admin actions in /admin → Logs tab
19. SQL upgraded to Standard S0:
    Verify in Azure Portal → SQL Database
    → Pricing tier shows Standard S0

### All
20. README updated:
    Risk profile ranges and defaults
    How to create and manage watchlists
    Admin access setup (Key Vault secret)
    How to invite family members
    What admin can and cannot do
