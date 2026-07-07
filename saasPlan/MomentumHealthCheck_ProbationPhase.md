# SwingTrader — Position Momentum Health
# Probation Phase and Early Exit Logic

## Context

The current exit logic has three triggers:
  Stop loss (price drops X% from entry)
  Target hit (price rises X% from entry)
  Time exit (position held > MaxHoldDays)

This creates a dead weight problem:
  A position that goes nowhere for 3 days
  occupies a slot and blocks capital
  from being deployed into better signals
  that arrive during that period.

The stop loss won't fire because the stock
hasn't dropped enough. The time exit won't
fire for 10 days. The position just sits.

This spec adds a fourth exit mechanism:
  Momentum Health Check — a lightweight
  daily score that measures whether the
  momentum that justified the entry is
  sustaining or fading.

Combined with a minimum hold period
(MinHoldDays) and maximum hold period
(MaxHoldDays) — already configurable
in the UI — positions now have a
two-phase lifecycle:

  Phase 1 — Probation (days 1 to MinHoldDays)
    Position must prove itself
    Momentum health checked on day MinHoldDays
    If score below threshold: exit immediately
    If score above threshold: promote to Confirmed

  Phase 2 — Confirmed (MinHoldDays+1 to MaxHoldDays)
    Thesis validated
    Normal exit rules only:
      Stop loss, target, trailing stop,
      MaxHoldDays time exit
    Momentum health NOT rechecked
    (committed — let it play out)

---

## New Enum: TradePhase

Add to SwingTrader.Core/Enums/Enums.cs:

```csharp
public enum TradePhase
{
    Probation,
    Confirmed,
    Exiting
}
```

---

## Trade Entity Changes

Add to SwingTrader.Core/Entities/Trade.cs:

```csharp
// Phase lifecycle
public TradePhase Phase { get; set; }
    = TradePhase.Probation;

public DateTime? PhaseConfirmedAt { get; set; }
    // Set when promoted from Probation
    // to Confirmed

// Momentum health (set on MinHoldDays check)
public decimal? MomentumHealthScore { get; set; }
    // 0.0 to 1.0

public string? MomentumHealthVerdict { get; set; }
    // "Confirmed", "Borderline", "Exit"

public string? MomentumHealthReasoning { get; set; }
    // One sentence explanation

public DateTime? MomentumHealthCheckedAt { get; set; }
```

Migration: AddMomentumHealthToTrade

```bash
dotnet ef migrations add AddMomentumHealthToTrade \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

---

## UserRiskProfile Changes

### New Fields

Add to UserRiskProfile entity:

```csharp
// ── Hold period (new — previously hardcoded) ──────

public int MinHoldDays { get; set; } = 3;
    // Probation period.
    // Position cannot be momentum-exited
    // before this many days have elapsed.
    // Stop loss and target still active
    // during this period.
    // Range: 1 to MaxHoldDays - 1
    // Must always be less than MaxHoldDays.

public int MaxHoldDays { get; set; } = 10;
    // Maximum hold period.
    // Position is force-closed at next
    // market open if still held at this
    // many days regardless of P&L.
    // Range: MinHoldDays + 1 to 30
    // Must always be greater than MinHoldDays.

// ── Momentum health threshold (new) ──────────────

public decimal MomentumHealthThreshold
    { get; set; } = 0.35m;
    // Momentum score below this on
    // MinHoldDays check → exit.
    // Range: 0.20 to 0.60
    // Lower = more permissive (keep more)
    // Higher = more aggressive (exit more)
```

### CapitalRules Additions

Add to SwingTrader.Core/Capital/CapitalRules.cs:

```csharp
// ── Hold period ranges and defaults ──────────────

public const int DefaultMinHoldDays = 3;
public const int AbsoluteMinHoldDays = 1;
    // Cannot be less than 1 day

public const int DefaultMaxHoldDays = 10;
public const int AbsoluteMaxHoldDays = 30;
    // Cannot be more than 30 days

// MinHoldDays must always be < MaxHoldDays.
// This is a cross-field constraint —
// enforced in Validate() not as individual
// range checks.

// ── Momentum health threshold ─────────────────────

public const decimal DefaultMomentumHealthThreshold
    = 0.35m;
public const decimal MinMomentumHealthThreshold
    = 0.20m;
public const decimal MaxMomentumHealthThreshold
    = 0.60m;
```

### UserRiskProfile.Validate() — Updated

Replace the existing Validate() method with
the complete version including all cross-field
validation. The critical addition is the
MinHoldDays < MaxHoldDays constraint:

```csharp
public void Validate()
{
    // ── Individual field ranges ───────────────────

    if (LockedCapitalPct < 0.50m ||
        LockedCapitalPct > 0.90m)
        throw new ValidationException(
            "Locked capital must be 50%-90%");

    if (MaxPositionPctOfActive < 0.05m ||
        MaxPositionPctOfActive > 0.33m)
        throw new ValidationException(
            "Max position must be 5%-33% " +
            "of active capital");

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

    if (MinHoldDays < 1)
        throw new ValidationException(
            "Probation period must be " +
            "at least 1 day");

    if (MaxHoldDays > 30)
        throw new ValidationException(
            "Maximum hold period cannot " +
            "exceed 30 days");

    if (MomentumHealthThreshold < 0.20m ||
        MomentumHealthThreshold > 0.60m)
        throw new ValidationException(
            "Momentum health threshold " +
            "must be 0.20-0.60");

    // ── Cross-field constraints ───────────────────
    // These must come AFTER individual range checks
    // so the error messages are specific and useful

    if (MinHoldDays >= MaxHoldDays)
        throw new ValidationException(
            $"Probation period ({MinHoldDays}d) " +
            $"must be less than maximum hold " +
            $"period ({MaxHoldDays}d). " +
            $"A position needs time to run " +
            $"after it passes probation.");

    // Sanity check: at least 1 confirmed day
    // MinHoldDays = 3, MaxHoldDays = 4 is valid
    // but means only 1 day of confirmed phase
    // Allow it — user's choice — but warn
    // via the UI (see Settings UI section)
    if (MaxHoldDays - MinHoldDays < 2)
    {
        // Not an error — allowed but suboptimal
        // UI shows a warning (see Settings section)
        // Validate() does not throw here
    }

    if (Tier2UnlockMinTrades <=
        Tier1UnlockMinTrades)
        throw new ValidationException(
            "Tier 2 min trades must exceed " +
            "Tier 1 min trades");

    var activePct = 1.0m - LockedCapitalPct;
    if (MaxPositionPctOfActive > activePct)
        throw new ValidationException(
            "Max position size exceeds " +
            "available active capital");
}
```

### API Validation

The PUT /api/risk-profile endpoint calls
profile.Validate() before saving.
On ValidationException return 400 with the
message as the error body:

```csharp
app.MapPut("/api/risk-profile",
    async (UserRiskProfile profile,
           IUserRiskProfileRepository repo,
           IUserContext userContext,
           CancellationToken ct) =>
{
    profile.UserId = userContext.UserId;

    try
    {
        profile.Validate();
    }
    catch (ValidationException ex)
    {
        return Results.BadRequest(new
        {
            error = ex.Message
        });
    }

    await repo.UpdateAsync(profile, ct);
    return Results.Ok(profile);
});
```

The error message is written to be
human-readable (e.g. "Probation period (5d)
must be less than maximum hold period (4d)")
so the Angular form can display it directly
without mapping to a friendlier string.

---

## New Service: IMomentumHealthService

Location: SwingTrader.Agents/Monitor/
          MomentumHealthService.cs

```csharp
public interface IMomentumHealthService
{
    Task<MomentumHealthResult>
        CalculateAsync(
            Trade trade,
            string symbol,
            CancellationToken ct);
}

public record MomentumHealthResult(
    decimal Score,
    string Verdict,
    string Reasoning,
    decimal RsiDirectionScore,
    decimal VolumeScore,
    decimal PriceDirectionScore,
    decimal RelativeStrengthScore);
```

### Score Calculation

Four components. No Claude call needed.
Uses data already fetched by the
Research Pipeline and Monitor Agent.

All data comes from:
  Latest StockSignal for the symbol
    (today's research output)
  Trade entity (entry price, entry date)
  Latest candle data (already in DB)

```csharp
public async Task<MomentumHealthResult>
    CalculateAsync(
        Trade trade,
        string symbol,
        CancellationToken ct)
{
    // Fetch today's signal for this symbol
    // (Research Agent has already run)
    var signal = await _signalRepo
        .GetLatestAsync(
            trade.UserId, symbol, ct);

    if (signal == null)
    {
        // Research hasn't run yet today
        // or symbol no longer on watchlist
        // Return neutral — don't exit
        // on missing data
        return new MomentumHealthResult(
            Score: 0.50m,
            Verdict: "Borderline",
            Reasoning: "Insufficient data " +
                "for momentum assessment — " +
                "holding position",
            RsiDirectionScore: 0.50m,
            VolumeScore: 0.50m,
            PriceDirectionScore: 0.50m,
            RelativeStrengthScore: 0.50m);
    }

    // ── Component 1: RSI Direction (0.30) ──────

    decimal rsiScore = 0.0m;
    if (signal.Rsi.HasValue)
    {
        var rsi = signal.Rsi.Value;
        // Is RSI rising? Compare to entry signal
        var entrySignal = await _signalRepo
            .GetByIdAsync(
                trade.SignalId ?? 0, ct);
        var entryRsi = entrySignal?.Rsi
            ?? rsi; // fallback to current

        bool rsiRising = rsi > entryRsi;

        rsiScore = (rsiRising, rsi) switch
        {
            (true, >= 50) => 1.00m,
            // Rising and above midpoint — strong
            (true, _)     => 0.50m,
            // Rising but below 50 — weak
            (false, _)    => 0.00m
            // Falling — not what we want
        };
    }
    else
    {
        rsiScore = 0.50m; // neutral if missing
    }

    // ── Component 2: Volume Sustainability (0.25) ──

    decimal volumeScore = 0.0m;
    if (signal.VolumeRatio.HasValue)
    {
        volumeScore = signal.VolumeRatio.Value switch
        {
            >= 0.8m => 1.00m,
            // Volume sustaining at 80%+ of average
            >= 0.5m => 0.50m,
            // Volume fading but not collapsed
            _       => 0.00m
            // Volume has dried up — no interest
        };
    }
    else
    {
        volumeScore = 0.50m;
    }

    // ── Component 3: Price vs Entry (0.25) ────────

    decimal priceScore = 0.0m;
    if (signal.CurrentPrice > 0)
    {
        var pctFromEntry = (signal.CurrentPrice
            - trade.EntryPrice)
            / trade.EntryPrice * 100m;

        priceScore = pctFromEntry switch
        {
            >= 1.5m  => 1.00m,
            // Clearly moving in right direction
            >= 0.0m  => 0.50m,
            // Positive but not convincingly
            _        => 0.00m
            // Negative from entry
        };
    }

    // ── Component 4: Relative to Sector (0.20) ────

    decimal relativeScore = 0.0m;
    if (signal.RelativeReturn.HasValue)
    {
        relativeScore = signal.RelativeReturn.Value switch
        {
            > 0.5m   => 1.00m,
            // Outperforming sector clearly
            >= -0.5m => 0.50m,
            // Roughly in line with sector
            _        => 0.00m
            // Underperforming sector
        };
    }
    else
    {
        relativeScore = 0.50m;
    }

    // ── Weighted total ─────────────────────────────

    var score =
        (rsiScore    * 0.30m) +
        (volumeScore * 0.25m) +
        (priceScore  * 0.25m) +
        (relativeScore * 0.20m);

    // ── Verdict ────────────────────────────────────
    // Thresholds are against the configured
    // MomentumHealthThreshold (default 0.35)
    // "Borderline" band is threshold to threshold+0.25

    var profile = await _riskProfileRepo
        .GetAsync(trade.UserId, ct);
    var threshold = profile
        .MomentumHealthThreshold;

    var verdict = score switch
    {
        _ when score >= (threshold + 0.25m)
            => "Confirmed",
        _ when score >= threshold
            => "Borderline",
        _   => "Exit"
    };

    // ── Reasoning sentence ─────────────────────────

    var parts = new List<string>();

    if (rsiScore >= 0.75m)
        parts.Add("RSI rising above 50");
    else if (rsiScore == 0)
        parts.Add("RSI falling");
    else
        parts.Add("RSI flat");

    if (volumeScore >= 0.75m)
        parts.Add("volume sustaining");
    else if (volumeScore == 0)
        parts.Add("volume faded");

    if (priceScore >= 0.75m)
    {
        var pct = Math.Round(
            (signal.CurrentPrice - trade.EntryPrice)
            / trade.EntryPrice * 100, 1);
        parts.Add($"+{pct}% from entry");
    }
    else if (priceScore == 0)
        parts.Add("price below entry");

    if (relativeScore >= 0.75m)
        parts.Add("outperforming sector");
    else if (relativeScore == 0)
        parts.Add("underperforming sector");

    var reasoning = verdict switch
    {
        "Confirmed" =>
            $"Thesis confirmed — " +
            string.Join(", ", parts) + ".",
        "Borderline" =>
            $"Mixed signals — " +
            string.Join(", ", parts) +
            ". One more day to prove direction.",
        _ =>
            $"Thesis not playing out — " +
            string.Join(", ", parts) + "."
    };

    return new MomentumHealthResult(
        Score: Math.Round(score, 3),
        Verdict: verdict,
        Reasoning: reasoning,
        RsiDirectionScore: rsiScore,
        VolumeScore: volumeScore,
        PriceDirectionScore: priceScore,
        RelativeStrengthScore: relativeScore);
}
```

---

## Integration: Where This Runs

The momentum health check runs once per day,
after the Research Agent completes.

The cleanest integration point is at the
end of the Research Pipeline's daily run,
after all signals are written to the DB.

### ResearchPipeline / ResearchFunction

After all 25 symbols have been researched,
add a new step:

```csharp
// After research completes for all symbols:
await CheckOpenPositionHealthAsync(ct);

private async Task CheckOpenPositionHealthAsync(
    CancellationToken ct)
{
    var openPositions = await _tradeRepo
        .GetOpenTradesAsync(ct);

    var profile = await _riskProfileRepo
        .GetAsync(_userContext.UserId, ct);

    var today = DateOnly.FromDateTime(
        DateTime.UtcNow);

    foreach (var trade in openPositions)
    {
        var daysHeld = (today.ToDateTime(
            TimeOnly.MinValue) - trade.OpenedAt)
            .Days;

        // Only check on MinHoldDays exactly
        // (not every day — just the gate)
        if (daysHeld != profile.MinHoldDays)
            continue;

        // Already confirmed — skip
        if (trade.Phase == TradePhase.Confirmed)
            continue;

        var result = await _momentumHealth
            .CalculateAsync(trade, trade.Symbol, ct);

        // Update trade record
        trade.MomentumHealthScore = result.Score;
        trade.MomentumHealthVerdict = result.Verdict;
        trade.MomentumHealthReasoning =
            result.Reasoning;
        trade.MomentumHealthCheckedAt =
            DateTime.UtcNow;

        if (result.Verdict == "Confirmed")
        {
            trade.Phase = TradePhase.Confirmed;
            trade.PhaseConfirmedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "{Symbol} momentum confirmed " +
                "(score {Score:F2}) — " +
                "letting run to day {Max}",
                trade.Symbol,
                result.Score,
                profile.MaxHoldDays);
        }
        else if (result.Verdict == "Borderline")
        {
            // Give one more day grace
            // Check again tomorrow
            // (daysHeld will be MinHoldDays+1)
            // We recheck on MinHoldDays+1 as well
            _logger.LogInformation(
                "{Symbol} borderline momentum " +
                "(score {Score:F2}) — " +
                "one more day",
                trade.Symbol, result.Score);
        }
        else // Exit
        {
            _logger.LogInformation(
                "{Symbol} failing momentum check " +
                "(score {Score:F2}) — " +
                "queuing early exit",
                trade.Symbol, result.Score);

            // Queue for exit at next market open
            // ExitReason set so Monitor Agent
            // knows why it was closed
            trade.ExitReason =
                "MomentumHealthExit";
        }

        await _tradeRepo.UpdateAsync(trade, ct);
    }
}
```

### Borderline Recheck Logic

For Borderline positions, recheck on
MinHoldDays + 1:

```csharp
// Extend the condition check:
if (daysHeld == profile.MinHoldDays ||
    (daysHeld == profile.MinHoldDays + 1 &&
     trade.MomentumHealthVerdict == "Borderline"))
{
    // Run the check
}
```

If still Borderline on day MinHoldDays+1:
treat as Exit — one grace day is enough.

---

## Integration: Monitor Agent

The Monitor Agent runs every 5 minutes.
It already checks stop loss and target.
Add a check for ExitReason == "MomentumHealthExit":

```csharp
// In MonitorService.CheckPositionAsync():

// Existing checks first:
// 1. Circuit breaker
// 2. Stop loss
// 3. Target hit
// 4. Trailing stop

// New check — momentum health exit queued:
if (trade.ExitReason == "MomentumHealthExit" &&
    IsMarketOpen(nowEt))
{
    _logger.LogInformation(
        "Executing momentum health exit " +
        "for {Symbol}", trade.Symbol);

    await _exitService.ExitPositionAsync(
        trade,
        exitReason: "Momentum health exit — " +
            trade.MomentumHealthReasoning,
        ct);

    return; // Don't check other conditions
}

// 5. Time exit (existing)
if (daysHeld >= profile.MaxHoldDays)
{
    await _exitService.ExitPositionAsync(
        trade, "Maximum hold time reached", ct);
}
```

The exit executes at next market open
(first Monitor cycle after 9:30am ET
where ExitReason is set).

---

## Daily Report Changes

The daily report email should surface
momentum health verdicts clearly.

### Open Positions Section

For each open position, add health status:

```
📂 Open Positions (3)

WMT · Holding £99.58 · 3d · -£1.07 (-1.06%)
⚠️ Momentum check: FAILING (0.21/1.0)
   Thesis not playing out — RSI falling,
   volume faded, price below entry,
   underperforming sector.
   → Queued for exit at market open

PLTR · Holding £103.19 · 3d · +£2.52 (+2.51%)
✅ Momentum check: CONFIRMED (0.74/1.0)
   Thesis confirmed — RSI rising above 50,
   volume sustaining, +2.4% from entry,
   outperforming sector.
   → Running to day 10

ORCL · Holding £103.20 · 2d · +£2.51 (+2.49%)
⏳ Momentum check: day 1 of 3 probation
   → Check on day 3
```

### New Positions Opened Today Section

When a slot is freed by a momentum exit
and a new position is opened the same day:

```
🔄 Position rotation today:
   Closed: WMT (momentum exit, -£1.07)
   Opened: RTX (conviction 7.8, 1.6:1 R:R)
   Net slot change: 0
   Capital redeployed: £99 → £103
```

---

## Settings UI Changes

### Risk Management Tab — Hold Period Section

This is a new section in the Risk Management
settings tab. The two fields are dependent —
MinHoldDays must always be less than
MaxHoldDays — so they must be presented
and validated together, not independently.

```
Hold Period
────────────────────────────────────────

Probation period
  [stepper: - 3 +]
  Min: 1   Max: MaxHoldDays - 1
    (upper bound is live — tracks
     whatever MaxHoldDays is currently set to)

  "Positions must show momentum within
   this many days or be exited.
   Stop loss and target remain active
   throughout."

Maximum hold period
  [stepper: - 10 +]
  Min: MinHoldDays + 1   Max: 30
    (lower bound is live — tracks
     whatever MinHoldDays is currently set to)

  "Positions are closed automatically
   if still open after this many days,
   regardless of profit or loss."

Visual timeline (updates live as
steppers change):

  Day 0              Day {MinHoldDays}        Day {MaxHoldDays}
  │                  │                        │
  ├──── Probation ───┤──── Confirmed ─────────┤
       {MinHoldDays}d     {MaxHoldDays - MinHoldDays}d

  Example at defaults (3 / 10):
  Day 0         Day 3              Day 10
  │             │                  │
  ├── Probation ┤──── Confirmed ───┤
       3 days        7 days
```

### Dependency Enforcement in Angular

The steppers are not independent.
Each one constrains the other in real time.

```typescript
// risk-management.component.ts

// When MinHoldDays changes:
onMinHoldDaysChange(value: number): void {
  this.form.get('minHoldDays')!.setValue(
    value, { emitEvent: false });

  // MaxHoldDays must be > MinHoldDays
  const currentMax = this.form.get(
    'maxHoldDays')!.value;

  if (currentMax <= value) {
    // Auto-adjust max to maintain
    // at least 1 confirmed day
    this.form.get('maxHoldDays')!
      .setValue(value + 1);

    this.showHoldDayWarning(
      `Maximum hold period adjusted to ` +
      `${value + 1} days to stay above ` +
      `probation period.`);
  }

  this.updateTimeline();
}

// When MaxHoldDays changes:
onMaxHoldDaysChange(value: number): void {
  this.form.get('maxHoldDays')!.setValue(
    value, { emitEvent: false });

  // MinHoldDays must be < MaxHoldDays
  const currentMin = this.form.get(
    'minHoldDays')!.value;

  if (currentMin >= value) {
    // Auto-adjust min to maintain validity
    this.form.get('minHoldDays')!
      .setValue(value - 1);

    this.showHoldDayWarning(
      `Probation period adjusted to ` +
      `${value - 1} days to stay below ` +
      `maximum hold period.`);
  }

  this.updateTimeline();
}
```

Auto-adjustment rather than blocking:
  User is trying to make a valid configuration
  Adjusting the other field to maintain
  validity is more helpful than just
  showing an error and making them
  fix it manually.
  The warning tells them what was adjusted.

### Warning: Short Confirmed Phase

If MaxHoldDays - MinHoldDays < 2,
show an amber advisory (not an error):

```
⚠️ Short confirmed phase

With a {MinHoldDays}-day probation and
{MaxHoldDays}-day maximum, confirmed
positions only have {MaxHoldDays - MinHoldDays}
day(s) to run after passing the check.
Consider increasing the maximum hold period
to give confirmed positions more time to
reach their target.

[This is a warning, not an error.
 Save Changes is still enabled.]
```

This guides users toward sensible
configurations without blocking them.

### Stepper Input Rules

```
MinHoldDays stepper:
  Display: "Probation period"
  Unit label: "days"
  Min value: 1 (hard floor)
  Max value: MaxHoldDays - 1 (live)
  Step: 1
  Cannot manually type a value >=
    MaxHoldDays — input rejects it
    and shows inline error:
    "Must be less than maximum hold
     period ({MaxHoldDays} days)"

MaxHoldDays stepper:
  Display: "Maximum hold period"
  Unit label: "days"
  Min value: MinHoldDays + 1 (live)
  Max value: 30 (hard ceiling)
  Step: 1
  Cannot manually type a value <=
    MinHoldDays — input rejects it
    and shows inline error:
    "Must be greater than probation
     period ({MinHoldDays} days)"
```

For typed input (if user types directly
into the stepper field rather than using
+/- buttons), validate on blur:

```typescript
validateHoldDayInput(
  field: 'min' | 'max',
  value: number
): string | null {

  if (field === 'min') {
    if (value < 1)
      return 'Minimum 1 day';
    if (value >= this.maxHoldDays)
      return `Must be less than maximum ` +
             `hold period (${this.maxHoldDays} days)`;
  }

  if (field === 'max') {
    if (value > 30)
      return 'Maximum 30 days';
    if (value <= this.minHoldDays)
      return `Must be greater than ` +
             `probation period ` +
             `(${this.minHoldDays} days)`;
  }

  return null; // valid
}
```

### Momentum Health Sensitivity Section

```
Momentum Health Sensitivity
────────────────────────────────────────

[Permissive ●────────○────────○ Aggressive]

                 Balanced (default)

"Higher sensitivity exits stalling positions
 sooner. Lower sensitivity gives positions
 more time to develop."

Below slider, live description:
  Permissive:
    "Exit only if all indicators clearly
     negative. Gives positions maximum
     benefit of the doubt."

  Balanced:
    "Exit if momentum is broadly negative.
     Balanced between opportunity cost
     and giving positions time to develop."

  Aggressive:
    "Exit unless momentum is clearly
     positive. Frees slots quickly for
     better opportunities."

At current setting a position needs
approximately {n} of 4 momentum
indicators positive to survive day
{MinHoldDays}.
  (calculated from threshold × 4,
   rounded to nearest integer)
```

Slider maps to threshold:
```
Permissive:  0.20
Balanced:    0.35 (default, centre position)
Aggressive:  0.50

Positions between labels interpolate linearly.
Store decimal in UserRiskProfile.
Display label in UI (reverse-map for display).
```

### Save Button State

```
[Save Changes] enabled when:
  Form is valid (no inline errors)
  Cross-field constraint satisfied
    (MinHoldDays < MaxHoldDays)
  At least one field differs from
    saved values (unsaved changes exist)

[Save Changes] disabled when:
  Any inline error is showing
  OR no changes have been made

Unsaved changes indicator:
  Shows diff for changed fields only:
    "Probation: 3d → 5d"
    "Max hold: 10d → 14d"
  Not shown for unchanged fields
```

---

## Dashboard Changes

### Open Positions Cards

Each position card gains a phase indicator:

```
┌─────────────────────────────────────┐
│ PLTR                    +£2.52 ✅   │
│ Day 3 · Confirmed                   │
│ ████████████░░  £134 → £139        │
│ Momentum: 0.74 · Running to day 10  │
└─────────────────────────────────────┘

┌─────────────────────────────────────┐
│ WMT                    -£1.07 ⚠️   │
│ Day 3 · Exiting at open             │
│ ████░░░░░░░░░░  £106 → £121        │
│ Momentum: 0.21 · Thesis failed      │
└─────────────────────────────────────┘

┌─────────────────────────────────────┐
│ ORCL                   +£2.51 ⏳   │
│ Day 2 · Probation (check tomorrow)  │
│ ████████░░░░░░  £133 → £151        │
│ Momentum: pending                   │
└─────────────────────────────────────┘
```

Phase badge colours:
  Probation: amber
  Confirmed: green
  Exiting:   red (pulsing)

---

## New API Endpoints

```csharp
// Get momentum health for all open positions
GET /api/positions/momentum-health
Returns: List<PositionMomentumDto>

public record PositionMomentumDto(
    int TradeId,
    string Symbol,
    TradePhase Phase,
    int DaysHeld,
    decimal? MomentumScore,
    string? MomentumVerdict,
    string? MomentumReasoning,
    DateTime? MomentumCheckedAt,
    DateTime? PhaseConfirmedAt);

// Manually trigger momentum check
// for a specific position (admin/debug)
POST /api/positions/{tradeId}/check-momentum
Returns: MomentumHealthResult
```

---

## Tests

### MomentumHealthServiceTests

```
Test: AllPositive_ReturnsConfirmed
  RSI rising above 50 (score 1.0)
  Volume 1.2x average (score 1.0)
  Price +2.5% from entry (score 1.0)
  Outperforming sector (score 1.0)
  Assert Score == 1.00
  Assert Verdict == "Confirmed"

Test: AllNegative_ReturnsExit
  RSI falling (score 0.0)
  Volume 0.4x average (score 0.0)
  Price -1.2% from entry (score 0.0)
  Underperforming sector (score 0.0)
  Assert Score == 0.00
  Assert Verdict == "Exit"

Test: Mixed_BelowThreshold_ReturnsExit
  RSI flat (score 0.5)
  Volume fading (score 0.5)
  Price +0.3% (score 0.5)
  In line with sector (score 0.5)
  Score = 0.50 * weights = 0.50
  Default threshold = 0.35
  0.50 >= 0.35 + 0.25 = 0.60? No
  0.50 >= 0.35? Yes
  Assert Verdict == "Borderline"

Test: MissingSignalData_ReturnsNeutral
  No signal found for symbol today
  Assert Score == 0.50
  Assert Verdict == "Borderline"
  (never exit on missing data)

Test: CustomThreshold_AffectsVerdict
  Score = 0.45
  Default threshold (0.35):
    0.45 >= 0.60? No → Borderline
  Aggressive threshold (0.50):
    0.45 >= 0.75? No
    0.45 >= 0.50? No → Exit
  Assert different verdicts for
    same score at different thresholds

Test: ReasoningContainsComponents
  RSI rising above 50
  Volume faded
  Assert Reasoning contains "RSI rising"
  Assert Reasoning contains "volume faded"

Test: Confirmed_SetsPhaseOnTrade
  Verdict = Confirmed
  Assert trade.Phase == Confirmed
  Assert trade.PhaseConfirmedAt != null

Test: Exit_SetsExitReasonOnTrade
  Verdict = Exit
  Assert trade.ExitReason ==
    "MomentumHealthExit"
  Assert trade.Phase == Probation
    (not changed to Confirmed)

Test: Borderline_NoPhaseChange
  Verdict = Borderline
  Assert trade.Phase == Probation
    (waiting for next day check)
  Assert trade.ExitReason == null

Test: DaysHeldCheck_OnlyFiresOnMinHoldDay
  MinHoldDays = 3
  Trade opened 2 days ago
  Assert CalculateAsync NOT called
  Trade opened 3 days ago
  Assert CalculateAsync IS called

Test: BorderlineGraceDay_ExitsIfStillBorderline
  Day 3: Verdict = Borderline
  Day 4: Verdict = Borderline again
  Assert trade.ExitReason ==
    "MomentumHealthExit"
    (no more grace days)

Test: ConfirmedPosition_SkippedOnSubsequentDays
  Trade Phase = Confirmed
  Day 5 research runs
  Assert CalculateAsync NOT called
  (confirmed positions not rechecked)
```

### MonitorServiceTests (additions)

```
Test: ExitReasonSet_ExecutesAtMarketOpen
  Trade.ExitReason = "MomentumHealthExit"
  Market is open (10am ET)
  Assert ExitPositionAsync called
  Assert exit reason in email contains
    "Momentum health exit"

Test: ExitReasonSet_WaitsIfMarketClosed
  Trade.ExitReason = "MomentumHealthExit"
  Market is closed (8pm ET)
  Assert ExitPositionAsync NOT called
  (waits for next market open)

Test: ExitReasonSet_BeforeStopLoss
  Trade.ExitReason = "MomentumHealthExit"
  Price also below stop loss
  Assert ExitPositionAsync called once
  Assert exit reason is momentum
    not stop loss
  (momentum check runs first in priority)
```

### ResearchPipelineTests (additions)

```
Test: HealthCheck_RunsAfterAllSignals
  3 open positions
  Research completes for all 25 symbols
  Assert CalculateAsync called 3 times
    (once per open position on day 3)

Test: HealthCheck_OnlyOnMinHoldDay
  MinHoldDays = 3
  Position opened today (day 0)
  Assert CalculateAsync NOT called

Test: HealthCheck_SkipsConfirmedPositions
  Position Phase = Confirmed
  Day 5 research runs
  Assert CalculateAsync NOT called

Test: HealthCheck_UsesConfiguredMinHoldDays
  MinHoldDays = 5 (not default 3)
  Trade opened 3 days ago
  Assert CalculateAsync NOT called
    (not yet day 5)
  Trade opened 5 days ago
  Assert CalculateAsync IS called
    (respects user's configured value)
```

### UserRiskProfileValidationTests (new)

```
Test: Valid_DefaultValues_PassesValidation
  MinHoldDays = 3, MaxHoldDays = 10
  Assert no exception thrown

Test: Valid_MinOneLessThanMax_PassesValidation
  MinHoldDays = 4, MaxHoldDays = 5
  Assert no exception thrown
  (1 confirmed day — tight but valid)

Test: Invalid_MinEqualsMax_ThrowsValidation
  MinHoldDays = 5, MaxHoldDays = 5
  Assert ValidationException thrown
  Assert message contains "must be less than"
  Assert message contains "5d"
    (includes the actual values so user
     knows exactly what was wrong)

Test: Invalid_MinGreaterThanMax_ThrowsValidation
  MinHoldDays = 7, MaxHoldDays = 5
  Assert ValidationException thrown
  Assert message contains "Probation period (7d)"
  Assert message contains "maximum hold period (5d)"

Test: Invalid_MinLessThanOne_ThrowsValidation
  MinHoldDays = 0, MaxHoldDays = 10
  Assert ValidationException thrown
  Assert message contains "at least 1 day"

Test: Invalid_MaxGreaterThan30_ThrowsValidation
  MinHoldDays = 3, MaxHoldDays = 31
  Assert ValidationException thrown
  Assert message contains "cannot exceed 30 days"

Test: Invalid_MaxSetTo1_MinAutoConstrains
  MaxHoldDays = 1
  MinHoldDays = 1 (equal to max)
  Assert ValidationException thrown
  (MinHoldDays must be < MaxHoldDays,
   so MinHoldDays = 0 would be needed,
   which violates the >= 1 rule — impossible)

Test: ApiEndpoint_InvalidCrossField_Returns400
  PUT /api/risk-profile
  Body: { minHoldDays: 8, maxHoldDays: 5 }
  Assert 400 BadRequest
  Assert response body contains
    "Probation period (8d) must be less
     than maximum hold period (5d)"

Test: ApiEndpoint_ValidValues_Returns200
  PUT /api/risk-profile
  Body: { minHoldDays: 3, maxHoldDays: 10 }
  Assert 200 OK
  Assert saved values match request

Test: ApiEndpoint_MinHoldDaysSaved_
      AffectsHealthCheckTiming
  Save minHoldDays = 5 via API
  Open a position
  Day 3 research: CalculateAsync NOT called
  Day 5 research: CalculateAsync IS called
  (end-to-end validation that the
   configured value is actually used)
```

### Angular UI Tests

```
Test: MaxHoldDays_CannotBeSetBelowMinHoldDays
  Set MinHoldDays = 7
  Try to set MaxHoldDays = 6
  Assert MaxHoldDays auto-adjusts
    to remain above MinHoldDays
    OR inline error shown

Test: MinHoldDays_CannotBeSetAboveMaxHoldDays
  Set MaxHoldDays = 4
  Try to set MinHoldDays = 5
  Assert MinHoldDays auto-adjusts
    to remain below MaxHoldDays
    OR inline error shown

Test: SaveButton_DisabledWhenInvalid
  Force MinHoldDays = MaxHoldDays
    (simulate direct input)
  Assert [Save Changes] button disabled
  Assert inline error visible

Test: SaveButton_EnabledWhenValid
  MinHoldDays = 3, MaxHoldDays = 10
  Assert [Save Changes] button enabled

Test: Timeline_UpdatesLiveWithSteppers
  MinHoldDays = 3, MaxHoldDays = 10
  Assert timeline shows:
    "Probation: 3 days"
    "Confirmed: 7 days"
  Change MinHoldDays to 5
  Assert timeline updates immediately:
    "Probation: 5 days"
    "Confirmed: 5 days"

Test: ShortConfirmedPhaseWarning_ShowsWhenNarrow
  MinHoldDays = 4, MaxHoldDays = 5
  Assert amber warning visible
  Assert warning mentions "1 day"
  Assert Save Changes still enabled
    (warning not blocking)

Test: ShortConfirmedPhaseWarning_HiddenWhenAdequate
  MinHoldDays = 3, MaxHoldDays = 10
  Assert no amber warning
```

---

## Migration

### EF Core Migration

```bash
dotnet ef migrations add AddMomentumHealthToTrade \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

This migration adds columns to two tables
that already contain live data.
The generated migration must be reviewed
and extended before applying — see
Data Migration Script below.

### Columns Added

**Trade table:**
```sql
Phase                    INT NOT NULL DEFAULT 0
  -- 0 = Probation
  -- All existing open trades start in
  -- Probation. They have not been through
  -- the momentum check yet regardless of
  -- how long they have been held.
  -- This is the correct safe default —
  -- the next research run will evaluate
  -- them on their current DaysHeld.

PhaseConfirmedAt         datetime2 NULL
  -- NULL for all existing trades.
  -- Set when promoted to Confirmed.

MomentumHealthScore      decimal(5,3) NULL
  -- NULL until first check runs.

MomentumHealthVerdict    nvarchar(20) NULL
  -- NULL until first check runs.

MomentumHealthReasoning  nvarchar(500) NULL
  -- NULL until first check runs.

MomentumHealthCheckedAt  datetime2 NULL
  -- NULL until first check runs.
```

**UserRiskProfile table:**
```sql
MinHoldDays              INT NOT NULL DEFAULT 3
  -- All existing users get the default
  -- probation period. No disruption.

MaxHoldDays              INT NOT NULL DEFAULT 10
  -- All existing users get the default
  -- maximum hold period. No disruption.

MomentumHealthThreshold  decimal(4,2) NOT NULL DEFAULT 0.35
  -- All existing users get balanced
  -- sensitivity. No disruption.
```

### Data Migration Script

The EF Core migration handles column
creation. This script handles the data
population for existing rows.

Run this AFTER applying the EF migration
and BEFORE deploying the new application
code. Running it after deployment is also
safe — the application handles NULL values
gracefully — but running it first is cleaner.

Location: scripts/MigrateMomentumHealth.sql

```sql
-- ============================================================
-- SwingTrader — Momentum Health Data Migration
-- Run after: dotnet ef database update (AddMomentumHealthToTrade)
-- Run before: deploying new application build
-- Safe to run multiple times (idempotent)
-- ============================================================

BEGIN TRANSACTION;

-- ── 1. Trade table: set Phase for existing rows ──────────────
--
-- All existing open trades → Probation (0)
-- All existing closed trades → Confirmed (1)
--   Closed trades have already resolved —
--   phase is historical context only,
--   not used for any active logic.
--   Probation would be incorrect for
--   a trade closed 2 weeks ago.

UPDATE Trades
SET Phase = CASE
    WHEN Status = 0  -- TradeStatus.Open
        THEN 0       -- TradePhase.Probation
    ELSE 1           -- TradePhase.Confirmed
        -- Closed/stopped/targeted trades
        -- are retroactively marked Confirmed
        -- as they ran their course
END
WHERE Phase IS NULL
   OR Phase = 0;
-- WHERE clause is defensive:
-- safe to re-run if migration was
-- partially applied

-- Verify:
SELECT
    CASE Phase
        WHEN 0 THEN 'Probation'
        WHEN 1 THEN 'Confirmed'
        WHEN 2 THEN 'Exiting'
    END AS PhaseName,
    COUNT(*) AS TradeCount,
    SUM(CASE WHEN Status = 0
        THEN 1 ELSE 0 END) AS OpenTrades,
    SUM(CASE WHEN Status != 0
        THEN 1 ELSE 0 END) AS ClosedTrades
FROM Trades
GROUP BY Phase;
-- Expected output:
--   Probation | n | n (open count) | 0
--   Confirmed | n | 0 | n (closed count)

-- ── 2. UserRiskProfile: populate new columns ─────────────────
--
-- Existing rows were created before these
-- columns existed. They need values now.
-- All users get the same defaults as new users.
-- No disruption to existing behaviour.

UPDATE UserRiskProfile
SET
    MinHoldDays = CASE
        WHEN MinHoldDays IS NULL THEN 3
        ELSE MinHoldDays  -- already set, leave it
    END,
    MaxHoldDays = CASE
        WHEN MaxHoldDays IS NULL THEN 10
        ELSE MaxHoldDays
    END,
    MomentumHealthThreshold = CASE
        WHEN MomentumHealthThreshold IS NULL
            THEN 0.35
        ELSE MomentumHealthThreshold
    END
WHERE
    MinHoldDays IS NULL
    OR MaxHoldDays IS NULL
    OR MomentumHealthThreshold IS NULL;

-- Verify:
SELECT
    UserId,
    MinHoldDays,
    MaxHoldDays,
    MomentumHealthThreshold
FROM UserRiskProfile
ORDER BY CreatedAt;
-- Every row should show non-NULL values.
-- No row should have MinHoldDays >= MaxHoldDays
-- (defaults 3/10 satisfy this).

-- ── 3. Verify cross-field constraint on existing data ─────────
--
-- Defensive check: ensure no existing profile
-- has MinHoldDays >= MaxHoldDays after migration.
-- Should never trigger with defaults but
-- worth checking if any profiles were
-- manually edited directly in the DB.

SELECT COUNT(*) AS InvalidProfiles
FROM UserRiskProfile
WHERE MinHoldDays >= MaxHoldDays;
-- Expected: 0
-- If non-zero: fix before deploying.

-- ── 4. Check for open trades older than MinHoldDays ──────────
--
-- Informational only — not a problem.
-- Open trades that have been held longer
-- than MinHoldDays will be evaluated
-- on the next research run. If they've
-- been held many days they may immediately
-- be promoted or exited. This is correct
-- behaviour — they should have been
-- evaluated earlier but the feature
-- didn't exist yet.

SELECT
    t.Symbol,
    t.UserId,
    t.OpenedAt,
    DATEDIFF(day, t.OpenedAt, GETUTCDATE())
        AS DaysHeld,
    rp.MinHoldDays,
    rp.MaxHoldDays,
    CASE
        WHEN DATEDIFF(day, t.OpenedAt, GETUTCDATE())
            >= rp.MaxHoldDays
            THEN 'Will be force-closed at next monitor cycle'
        WHEN DATEDIFF(day, t.OpenedAt, GETUTCDATE())
            >= rp.MinHoldDays
            THEN 'Will be evaluated at next research run'
        ELSE 'Still in probation period'
    END AS ExpectedBehaviour
FROM Trades t
JOIN UserRiskProfile rp ON rp.UserId = t.UserId
WHERE t.Status = 0  -- open only
ORDER BY t.OpenedAt;

COMMIT TRANSACTION;

-- ============================================================
-- Migration complete.
-- Review the SELECT outputs above before deploying.
-- All counts should match expectations.
-- ============================================================
```

### Running the Script

**Azure SQL via Azure Data Studio or portal:**
```
1. Open Azure Data Studio
2. Connect to swingtrader-sql-prod
3. Open scripts/MigrateMomentumHealth.sql
4. Review the SELECT outputs
5. If all look correct: the COMMIT
   at the end applies everything
6. If anything looks wrong: change
   COMMIT to ROLLBACK and investigate
```

**Via GitHub Actions (automated):**

Add to deploy-infra.yml after the
EF migrations step:

```yaml
- name: Run data migration script
  run: |
    sqlcmd \
      -S swingtrader-sql-prod.database.windows.net \
      -d swingtrader-db \
      -G \
      -i scripts/MigrateMomentumHealth.sql \
      -o migration-output.txt

    cat migration-output.txt

    # Check for the InvalidProfiles count
    # If non-zero the script outputs a warning
    if grep -q "InvalidProfiles" migration-output.txt; then
      echo "Data migration completed"
    fi
```

Note: sqlcmd uses Azure AD authentication
(-G flag) via the deployment service principal
which already has db_owner access.

### Safe to Re-Run

The script is idempotent:
- WHERE clauses only update NULL values
- Running twice produces identical results
- No data is deleted
- No existing values are overwritten

If the deployment fails partway through
and needs to be re-run, the script
can be applied again safely.

### Rollback

If the deployment needs to be rolled back:

```sql
-- Remove new columns from Trade
ALTER TABLE Trades DROP COLUMN Phase;
ALTER TABLE Trades DROP COLUMN PhaseConfirmedAt;
ALTER TABLE Trades DROP COLUMN MomentumHealthScore;
ALTER TABLE Trades DROP COLUMN MomentumHealthVerdict;
ALTER TABLE Trades DROP COLUMN MomentumHealthReasoning;
ALTER TABLE Trades DROP COLUMN MomentumHealthCheckedAt;

-- Remove new columns from UserRiskProfile
ALTER TABLE UserRiskProfile
    DROP COLUMN MinHoldDays;
ALTER TABLE UserRiskProfile
    DROP COLUMN MaxHoldDays;
ALTER TABLE UserRiskProfile
    DROP COLUMN MomentumHealthThreshold;
```

Only needed if rolling back the application
deployment entirely. Not needed for normal
operation.
---

## DI Registration

```csharp
services.AddScoped<IMomentumHealthService,
    MomentumHealthService>();
```

---

## Deliverables

1. dotnet test — all tests green
   New momentum health tests passing
   New validation tests passing

2. Data migration script reviewed and run:
   scripts/MigrateMomentumHealth.sql executed
   SELECT outputs verified:
     All open trades → Phase = Probation
     All closed trades → Phase = Confirmed
     All UserRiskProfile rows have
       MinHoldDays = 3
       MaxHoldDays = 10
       MomentumHealthThreshold = 0.35
     InvalidProfiles count = 0

3. Open a demo position.
   Wait for MinHoldDays to elapse.
   Verify in Application Insights logs:
   "Checking momentum health for {Symbol}"
   appearing after research completes

4. Verify trade record updated:
   SELECT Symbol, Phase,
     MomentumHealthScore,
     MomentumHealthVerdict,
     MomentumHealthReasoning
   FROM Trades
   WHERE Status = 0  -- open
   Should show score and verdict
   on day MinHoldDays

5. For a position that fails the check:
   Verify ExitReason = "MomentumHealthExit"
   set on trade record
   Verify Monitor Agent picks it up
   at next market open cycle
   Verify position closed and email sent

6. Daily report email on day MinHoldDays:
   Open positions section shows:
   ✅ CONFIRMED with score for winners
   ⚠️ EXITING with reasoning for failures
   ⏳ PROBATION for positions not yet checked

7. Dashboard position cards show:
   Phase badge (Probation/Confirmed/Exiting)
   Momentum score (on day MinHoldDays+)
   Correct colour coding

8. Settings → Risk Management hold period:
   Probation stepper works (1 to MaxHoldDays-1)
   Max hold stepper works (MinHoldDays+1 to 30)
   Live timeline updates as steppers change
   Auto-adjustment fires when values conflict:
     Push MinHoldDays up to MaxHoldDays
     MaxHoldDays auto-increments
   Short confirmed phase warning shows
     when MaxHoldDays - MinHoldDays < 2
   Save Changes disabled when invalid

9. Settings → Momentum sensitivity slider:
   Three positions (Permissive/Balanced/Aggressive)
   Change to Aggressive
   Verify MomentumHealthThreshold = 0.50 in DB
   Change back to Balanced
   Verify threshold = 0.35 in DB

10. API validation:
    PUT /api/risk-profile with
      { minHoldDays: 8, maxHoldDays: 5 }
    Assert 400 response
    Assert error message contains
      "Probation period (8d) must be less
       than maximum hold period (5d)"

11. HealthCheck respects configured MinHoldDays:
    Change MinHoldDays to 5 in Settings
    Open a new position
    Verify no health check on day 3
    Verify health check fires on day 5

12. README updated:
    What the probation phase is
    How the momentum health score works
    What each verdict means
    How to tune the sensitivity slider
    How to run the data migration script
    What happens to existing positions
      after the migration

---

## Reference: Legacy Repo

For exit execution pattern:
  C:\Code\swingTrader-v1\SwingTrader.Agents\
  Monitor\MonitorService.cs
  ExitPositionAsync method

For signal retrieval pattern:
  C:\Code\swingTrader-v1\SwingTrader.Data\
  Repositories\StockSignalRepository.cs

For risk profile loading pattern:
  C:\Code\swingTrader-v1\SwingTrader.Agents\
  Risk\TierEvaluationService.cs

The momentum health calculation itself
is new — no legacy equivalent exists.
Build fresh.

---

## What This Does NOT Include

  No changes to stop loss logic
  No changes to target hit logic
  No changes to trailing stop logic
  No changes to the conviction scoring
    used for entry decisions
  No Claude API calls (pure calculation)
  No external API calls beyond what
    research already fetches
  No changes to watchlist or
    refinement systems
  Confirmed positions run exactly
    as before — only the probation
    phase is new behaviour
