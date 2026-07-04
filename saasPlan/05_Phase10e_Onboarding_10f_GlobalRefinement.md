# SwingTrader — Phase 10e: Onboarding Flow

## Context
Phase 10d complete. Users can sign in,
store encrypted API keys, and have their
own research pipeline running with
their own credentials.

But the experience for a new user landing
for the first time is rough:
  Sign in → empty dashboard
  No guidance on what to do next
  Settings page requires knowing
  which API keys to get and where

Phase 10e adds a structured onboarding
flow that guides new users from first
sign-in to first research run.

## Onboarding Trigger

On first login, UserRegistrationMiddleware
creates the AppUser record.

Angular detects this is a new user:
  GET /api/user/profile returns
  { isOnboarded: false }

Router guards redirect to /onboarding
instead of /dashboard until
isOnboarded = true.

## Onboarding Steps

Five-step wizard.
Angular Material Stepper component.
Can go back to previous steps.
Progress saved — can close and resume.

### Step 1 — Welcome

Title: "Welcome to SwingTrader"

Content:
  Brief explanation of what the system does:
  "SwingTrader researches stocks every morning,
   sends you a daily brief, and with your
   approval places trades automatically.

   To get started you'll need:
   • A Trading 212 account (free)
   • A Finnhub API key (free)
   • A Tiingo API key (free)
   • A Gmail address for daily reports

   This takes about 10 minutes."

[Let's get started →]

### Step 2 — Trading 212

Title: "Connect Trading 212"

Two substeps:

Substep A — Get API credentials:
  "1. Open the Trading 212 app
   2. Go to Settings → API (Beta)
   3. Make sure you're in your
      INVEST account, NOT your ISA
   4. Generate an API key
   5. Copy the key and secret below"

  Embedded link: [Open T212 website →]

  Fields:
    API Key [password input]
    API Secret [password input]
  [Test connection] button
    → Shows: "✅ Connected to account
      ID: 12345678 (Demo mode)"
    → Or: "❌ Connection failed: Invalid key"

  Warning card:
  "⚠️ IMPORTANT: Only connect your Invest
   account, never your ISA.
   The account ID above should match
   what you see in the T212 app under
   your Invest account."

  Checkbox (required to proceed):
  ☐ I confirm this is my Invest account,
    not my Stocks & Shares ISA.

Substep B — Choose mode:
  "Start with a demo account — trades
   real-looking but use virtual money.
   Recommended for at least the first month."

  [● Demo mode — start safely]
  [○ Live mode — real money]

  If Live selected: additional warning:
  "Live trading uses real money.
   We strongly recommend running in demo
   mode for at least 30 days first to
   validate the system is working correctly."

  Checkbox (if Live selected):
  ☐ I understand this will place real trades
    with real money.

### Step 3 — Market Data

Title: "Connect Market Data"

Two API keys needed.

Finnhub:
  "Provides stock news and quote data.
   Get your free key at finnhub.io"

  [Open Finnhub →] button (opens new tab)
  Instructions:
    "1. Create a free account
     2. Go to Dashboard
     3. Copy your API key"

  [API Key input] [Test ✓]

Tiingo:
  "Provides historical price data.
   Get your free key at tiingo.com"

  [Open Tiingo →] button
  Instructions:
    "1. Create a free account
     2. Go to Account → API
     3. Copy your token"

  [Token input] [Test ✓]

Both must show ✓ before proceeding.

### Step 4 — Email Setup

Title: "Set Up Your Daily Report"

"Every market morning, SwingTrader sends
 a report with buy signals, open positions,
 and portfolio performance.

 You'll need a Gmail address and a
 Gmail App Password (not your regular password)."

Primary email: pre-filled from Google sign-in
  [Mark this as read-only, with note:
   "Using your Google account email"]

Additional recipients (optional):
  [Add another email address]
  (Kelly's address entered here)

Gmail App Password setup:
  Collapsible instructions:
  "1. Go to myaccount.google.com
   2. Security → 2-Step Verification
      (must be enabled)
   3. App passwords
   4. Select app: Mail
   5. Select device: Other (SwingTrader)
   6. Copy the 16-character password"

  [Open Google Account →] button

  Fields:
    Gmail address [input]
    App password [password input]
  [Test — send a test email] button

On test:
  Sends an email to the address:
  Subject: "SwingTrader — email test"
  Body: "Your SwingTrader email is
         configured correctly."
  Shows: "✅ Test email sent — check inbox"

### Step 5 — All Done

Title: "You're set up!"

Summary card:
  ✅ Trading 212: Demo account connected
  ✅ Finnhub: Connected
  ✅ Tiingo: Connected
  ✅ Email: Configured

"Your first daily report will arrive
 tomorrow at 6:30 AM ET (11:30 AM UK time).

 In the meantime, you can explore the
 dashboard and configure your watchlist
 and trading preferences in Settings."

What happens next:
  "Tonight: Watchlist Agent selects
   25 stocks to watch
   Tomorrow 6:00 AM ET: Research Agent
   analyses each stock
   Tomorrow 6:30 AM ET: Daily report
   lands in your inbox
   Tomorrow 9:20 AM ET: Execution Agent
   requests approval (if enabled)"

[Go to Dashboard →]

On click:
  PUT /api/user/complete-onboarding
  Sets isOnboarded = true on AppUser
  Router navigates to /dashboard
  Onboarding guard no longer redirects

## Onboarding Guard

src/app/core/guards/onboarding.guard.ts:

export const onboardingGuard:
  CanActivateFn = async (route, state) => {

  const auth = inject(AuthService);
  const api = inject(ApiService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login']);
  }

  const profile = await firstValueFrom(
    api.getUserProfile());

  if (!profile.isOnboarded &&
      route.url[0]?.path !== 'onboarding') {
    return router.createUrlTree(
      ['/onboarding']);
  }

  return true;
};

Apply to all routes except login and onboarding.

## Onboarding State Persistence

Each step completion saves to the API:
  POST /api/user/onboarding-progress
  Body: { step: 2, completed: true }

If user closes browser mid-onboarding:
  On return, GET /api/user/onboarding-progress
  Resumes from last completed step

AppUser additions:
  OnboardingStep (int) — last completed
  IsOnboarded (bool)

## Admin: View All Users (You Only)

Simple admin panel accessible to
a specific UserId (yours, hardcoded
in config).

GET /api/admin/users
  Lists all AppUsers with:
  Name, Email, JoinedAt, LastLogin,
  IsOnboarded, T212Mode, KeyStatuses

GET /api/admin/user/{userId}/status
  Full status for a specific user

POST /api/admin/user/{userId}/reset-onboarding
  Forces onboarding to restart

Only your UserId can access /api/admin/*.
Hard check in middleware — not role-based.

Angular route: /admin (guarded to admin only)
Simple table showing all users.

## Tests

Test: NewUser_RedirectedToOnboarding
  New user signs in (isOnboarded=false)
  Navigates to /dashboard
  Assert redirected to /onboarding

Test: OnboardedUser_AccessesDashboard
  Existing user (isOnboarded=true)
  Navigates to /dashboard
  Assert dashboard shown (no redirect)

Test: OnboardingComplete_SetsFlag
  POST /api/user/complete-onboarding
  Assert AppUser.IsOnboarded = true

Test: StepProgress_Persisted
  Complete step 2 in onboarding
  Close browser (simulate)
  Return to onboarding
  Assert starts at step 2 not step 1

Test: T212Connection_RequiresISAConfirmation
  Try to proceed without checking ISA checkbox
  Assert cannot advance to next step

## Deliverables

1. New user sign-in → /onboarding
2. All 5 steps render correctly
3. T212 test connection works
4. Finnhub and Tiingo test works
5. Test email sends and arrives
6. Completing step 5 → /dashboard
7. Returning user → /dashboard directly
8. Admin page shows your user account
9. README: onboarding instructions
   for friends and family

---

# SwingTrader — Phase 10f: Global Refinement

## Context
Phase 10e complete. Multiple users
onboarded and trading.
Each user's Refinement Agent runs monthly
on their own 40+ trades.

Phase 10f adds a global refinement layer:
anonymised trade data pooled across all
opted-in users to produce higher-confidence
weight suggestions.

## Privacy Architecture

What is shared (anonymised):
  Per-trade record:
    All 8 component scores (0.0-1.0)
    ConvictionScore
    MarketRegimeAtEntry
    SpyReturnDuringTrade
    TradeReturnPct (return percentage)
    HoldDays
    SetupType (enum)
    WasWin (bool)

What is NEVER shared:
  Symbol (which stock was traded)
  EntryPrice / ExitPrice (actual prices)
  Quantity (position size)
  UserId (cannot link back to a person)
  Any PII or account information

Each contributing record is a
statistical data point only.
An attacker with the full dataset
cannot determine:
  Who traded it
  What they traded
  How much money was involved

## Opt-In Mechanism

Default: opted OUT.
User explicitly opts in via Settings.

AppUser addition:
  bool GlobalRefinementOptIn = false

Settings → Account tab:
  "Contribute to Global Insights"

  Toggle: [OFF] / [ON]

  Explanatory text when toggling on:
  "You'll share anonymised trading
   performance data (not symbols or
   prices) with other SwingTrader users.
   In return, you get access to weight
   suggestions based on hundreds of trades
   rather than just your own.

   What's shared: component scores,
   trade outcomes, market conditions.
   What's never shared: which stocks
   you traded, your account details,
   or any personal information."

  [Enable global insights] confirmation button

## Global Data Collection

New entity: AnonymisedTradeRecord

public class AnonymisedTradeRecord : BaseEntity
{
  // No UserId — deliberately anonymous
  public string AnonymousContributorId { get; set; }
    // SHA256 hash of (UserId + salt)
    // Allows deduplication without
    // identifying the user

  public decimal RsiScore { get; set; }
  public decimal MacdScore { get; set; }
  public decimal VolumeScore { get; set; }
  public decimal SentimentScore { get; set; }
  public decimal SetupQualityScore { get; set; }
  public decimal RelativeStrengthScore { get; set; }
  public decimal PriceLevelScore { get; set; }
  public decimal FundamentalScore { get; set; }
  public decimal ConvictionScore { get; set; }
  public SetupType SetupType { get; set; }
  public MarketRegime MarketRegime { get; set; }
  public decimal? SpyReturnDuringTrade { get; set; }
  public decimal TradeReturnPct { get; set; }
  public int HoldDays { get; set; }
  public bool WasWin { get; set; }
  public DateOnly TradeMonth { get; set; }
    // Month only — not exact date
    // Further reduces identifiability
}

Stored in a separate schema/table
from all user data.
No foreign keys to any user tables.

## Contribution Process

Monthly, after each user's Refinement
Agent runs (16th of month —
one day after individual runs):

For each opted-in user:
  Load their closed trades from last 90 days
  For each trade:
    Create AnonymisedTradeRecord
    Hash UserId with salt for contributor ID
    Do NOT include symbol, price, quantity
  Bulk insert to AnonymisedTradeRecords table

This runs as a Function:
  GlobalContributionFunction
  TimerTrigger: 0 0 14 16 * *
    (2pm UTC 16th of month)

## Global Refinement Analysis

GlobalRefinementFunction:
  TimerTrigger: 0 0 15 17 * *
    (3pm UTC 17th of month —
     after contributions complete)

  Load all AnonymisedTradeRecords
    from last 90 days
  Run ComponentCorrelationService
    (same as per-user, just more data)
  Generate GlobalWeightSuggestion:
    Same structure as RefinementSuggestion
    But marked IsGlobal=true
    Includes contributingRecords count
    Includes unique contributor count
      (count of distinct AnonymousContributorIds)

  No Claude call for global narrative
    (keep costs down — template only)

GlobalWeightSuggestion entity additions:
  bool IsGlobal = true
  int ContributingRecords (total trades)
  int UniqueContributors (distinct users)

## Surfacing Global Suggestions

GET /api/refinement/global
Returns latest global suggestion
(if user is opted in, else 404)

Angular Refinement page additions:

New section: "Global Insights"
  Only visible if opted in

  Shows:
    Contributing instances: 7
    Total trades analysed: 847
    Confidence: High

    Component comparison:
      [Your weights] vs [Global suggestion]
      Side by side bars

    [Apply Global Weights] button
    "Overrides your personal weights
     with the crowd-sourced suggestion"

    [Apply Blend (50/50)] button
    "Blends your personal weights
     with global suggestion equally"

## Confidence Levels for Global

Based on trade count across all contributors:

  < 100 records: Low
  100-500 records: Medium
  500+ records: High

At 10 users × 60 trades = 600 records
within the first year, Medium-High
confidence becomes achievable.

## New API Endpoints

GET /api/refinement/global
  Returns latest global suggestion
  Requires opt-in (403 if not opted in)

POST /api/user/global-opt-in
  { "optIn": true/false }
  Updates AppUser.GlobalRefinementOptIn

POST /api/refinement/apply-global
  Applies global weights for current user
  Same Apply mechanism as personal

GET /api/admin/global-stats
  Admin only
  Shows contributor count, record count,
  last run date, confidence level

## Tests

Test: AnonymisedRecord_ContainsNoUserId
  Create anonymised record for user A
  Assert UserId not present in record
  Assert AnonymousContributorId is a hash
  Assert hash != userId string

Test: TwoUsersHashDifferently
  User A and User B contribute
  Assert distinct AnonymousContributorIds

Test: SameUserConsistentHash
  User A contributes twice
  Assert same AnonymousContributorId
  (deduplication works)

Test: OptedOutUser_DataNotContributed
  User with GlobalRefinementOptIn=false
  Run GlobalContributionFunction
  Assert no records for this user's trades

Test: GlobalSuggestion_RequiresOptIn
  GET /api/refinement/global
  As non-opted-in user
  Assert 403

Test: ApplyGlobal_UpdatesUserWeights
  Apply global suggestion
  Assert StrategyWeights row created
  with user's UserId and new weights

## Deliverables

1. Settings shows opt-in toggle
2. Opted-in user contributes records
   after monthly run
3. Records in AnonymisedTradeRecords
   contain no identifying information
4. Global Insights section visible
   on Refinement page for opted-in users
5. Apply Global Weights works
6. Admin stats show contributor count

## Complete Phase 10 Summary

Phase 10a: Azure infrastructure
  Container Apps, Functions, Azure SQL,
  Key Vault, Container Registry,
  GitHub Actions CI/CD, backup removed

Phase 10b: Angular frontend
  Material + AG Grid + Charts,
  Dashboard, Signals, Trades,
  Refinement, Readiness pages,
  Auto-polling, responsive design

Phase 10c: Multi-tenancy + auth
  Google Sign-On via Azure AD B2C,
  UserId on all tables,
  Row-level data isolation,
  JWT validation, MSAL Angular

Phase 10d: Key encryption + scheduling
  Per-user AES-256 + Key Vault encryption,
  Service Bus queue-based scheduling,
  Per-user HTTP clients,
  Settings page fully implemented

Phase 10e: Onboarding
  5-step guided setup wizard,
  T212 connection with ISA warning,
  API key setup with deep links,
  Email test confirmation,
  Admin user management

Phase 10f: Global refinement
  Anonymised trade data pooling,
  Opt-in mechanism with clear disclosure,
  Global weight suggestions,
  Crowd-sourced confidence scoring

## Estimated Total Cost at 10 Users

Container Apps (API):    £0
Function App:            £0
Azure SQL Standard S0:   £12
Key Vault:               £3-4
Container Registry:      £4
Service Bus:             £0.05
Claude API (shared):     £15
  (~£1.50/user/month)
────────────────────────────
Total:                  ~£34/month
Per user:               ~£3.40/month

If users provide own Claude keys:
Total without Claude:   ~£19/month
Per user:               ~£1.90/month

## README: Invitation Instructions
(For when inviting family members)

"To join SwingTrader:

1. Go to https://{app-url}
2. Click 'Sign in with Google'
3. Follow the 5-step setup (10 minutes)
4. You'll need:
   - A Trading 212 account (free)
     Get it at trading212.com
     Use the Invest account, NOT the ISA
   - A Finnhub API key (free)
     Get it at finnhub.io
   - A Tiingo API key (free)
     Get it at tiingo.com
   - A Gmail address for daily reports

All your API keys are encrypted and
only used to run your own trades.
Start in Demo mode — no real money
until you're comfortable with how it works."
