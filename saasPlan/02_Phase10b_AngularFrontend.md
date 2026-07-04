# SwingTrader — Phase 10b: Angular Frontend

## Context
Phase 10a complete. System running in Azure.
ASP.NET Core API serving existing HTML/JS pages.
Angular replaces all existing static HTML files.
The API endpoints are unchanged — Angular
consumes the same JSON the existing pages use.

## Technology Stack

Angular 17+ (standalone components)
Angular Material (UI shell, forms, cards)
AG Grid Community (data tables)
Chart.js with ng2-charts (charts)
RxJS (async data streams)
Angular Signals (component-local state)
MSAL Angular (auth shell — inactive until 10c)
TypeScript strict mode throughout

## Project Structure

Angular project lives inside the solution:

SwingTrader.Angular/
  (Angular workspace root)
  angular.json
  package.json
  tsconfig.json
  src/
    app/
      core/
        interceptors/
          auth.interceptor.ts
          error.interceptor.ts
        guards/
          auth.guard.ts
        services/
          api.service.ts
          auth.service.ts
        models/
          (TypeScript interfaces matching API DTOs)
      shared/
        components/
          regime-badge/
          conviction-bar/
          stop-target-bar/
          sparkline/
          loading-spinner/
          error-card/
        pipes/
          currency-gbp.pipe.ts
          percent-signed.pipe.ts
          relative-time.pipe.ts
      features/
        dashboard/
          dashboard.component.ts
          dashboard.component.html
          dashboard.component.scss
        signals/
          signals.component.ts
          signal-card/
          signal-card.component.ts
        trades/
          trades.component.ts
          open-positions/
          trade-history/
        refinement/
          refinement.component.ts
          weight-editor/
          suggestion-card/
        readiness/
          readiness.component.ts
          feature-card/
          milestone-timeline/
        settings/
          settings.component.ts
          (placeholder for Phase 10e)
      app.component.ts
      app.component.html
      app.routes.ts
    assets/
    environments/
      environment.ts
      environment.prod.ts
    styles.scss
    main.ts
    index.html

## Angular Build Integration with ASP.NET Core

The Angular build output goes into
SwingTrader.Api/wwwroot.

Build pipeline:
  1. ng build --output-path ../SwingTrader.Api/wwwroot
  2. dotnet publish SwingTrader.Api
  3. Both are in the same Docker image

angular.json outputPath:
  "../SwingTrader.Api/wwwroot"

SwingTrader.Api Program.cs already has:
  app.UseDefaultFiles();
  app.UseStaticFiles();
  app.MapFallbackToFile("index.html");

This serves Angular's index.html for all
non-API routes. Angular Router handles
client-side navigation.

GitHub Actions: api deployment workflow
runs ng build before dotnet publish:
  - run: npm ci
    working-directory: SwingTrader.Angular
  - run: ng build --configuration production
    working-directory: SwingTrader.Angular
  - run: dotnet publish SwingTrader.Api ...

## TypeScript Models

Generate from Swagger automatically.
Add to SwingTrader.Api:
  dotnet add package Swashbuckle.AspNetCore

Access Swagger JSON at:
  /swagger/v1/swagger.json

Use openapi-typescript-codegen to generate:
  npm install -g openapi-typescript-codegen
  openapi --input http://localhost:5001/swagger/v1/swagger.json \
    --output src/app/core/models/generated \
    --client angular

Regenerate whenever API DTOs change.
Never hand-write interfaces that duplicate C# DTOs.

## Colour Palette and Theme

Angular Material custom theme.
Consistent with existing dashboard
dark navy aesthetic.

src/styles.scss:

@use '@angular/material' as mat;

$primary: mat.define-palette(
  mat.$blue-palette, 600);
$accent: mat.define-palette(
  mat.$green-palette, 600);
$warn: mat.define-palette(
  mat.$red-palette, 600);

$theme: mat.define-dark-theme((
  color: (
    primary: $primary,
    accent: $accent,
    warn: $warn,
  ),
  typography: mat.define-typography-config(
    $font-family: '-apple-system, BlinkMacSystemFont,
      "Segoe UI", Roboto, sans-serif'
  ),
  density: 0,
));

@include mat.all-component-themes($theme);

CSS custom properties:
  --st-navy:    #0f172a
  --st-card:    #1e293b
  --st-border:  #334155
  --st-text:    #f1f5f9
  --st-muted:   #94a3b8
  --st-green:   #22c55e
  --st-red:     #ef4444
  --st-amber:   #f59e0b
  --st-blue:    #3b82f6

## Core Services

### ApiService

src/app/core/services/api.service.ts

Injectable({ providedIn: 'root' })
export class ApiService {

  private readonly baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // Portfolio
  getPortfolio(): Observable<PortfolioDto> {
    return this.http.get<PortfolioDto>(
      `${this.baseUrl}/api/portfolio`);
  }

  // Positions
  getPositions(): Observable<PositionDto[]> {
    return this.http.get<PositionDto[]>(
      `${this.baseUrl}/api/positions`);
  }

  // Signals
  getSignalsToday():
    Observable<SignalGroupDto> {
    return this.http.get<SignalGroupDto>(
      `${this.baseUrl}/api/signals/today`);
  }

  // Trades
  getRecentTrades(days = 30):
    Observable<TradeDto[]> {
    return this.http.get<TradeDto[]>(
      `${this.baseUrl}/api/trades/recent`,
      { params: { days } });
  }

  // Status
  getStatus(): Observable<StatusDto> {
    return this.http.get<StatusDto>(
      `${this.baseUrl}/api/status`);
  }

  // Refinement
  getRefinementStatus():
    Observable<RefinementStatusDto> {
    return this.http.get<RefinementStatusDto>(
      `${this.baseUrl}/api/refinement/status`);
  }

  applyRefinement(suggestionId: number):
    Observable<ApplyResultDto> {
    return this.http.post<ApplyResultDto>(
      `${this.baseUrl}/api/refinement/apply`,
      { suggestionId });
  }

  rejectRefinement(
    suggestionId: number,
    note: string): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/api/refinement/reject`,
      { suggestionId, note });
  }

  // Readiness
  getReadiness():
    Observable<ReadinessReportDto> {
    return this.http.get<ReadinessReportDto>(
      `${this.baseUrl}/api/readiness`);
  }

  completeChecklist(
    checkName: string,
    notes?: string): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/api/readiness/complete-checklist`,
      { checkName, notes });
  }

  // Manual triggers
  runAgent(agent: string):
    Observable<RunResultDto> {
    return this.http.post<RunResultDto>(
      `${this.baseUrl}/run/${agent}`,
      {});
  }

  // Approval
  approve(token: string,
    symbols?: string): Observable<void> {
    const params = symbols
      ? { token, symbols }
      : { token };
    return this.http.get<void>(
      `${this.baseUrl}/approve`,
      { params });
  }

  // Current regime
  getCurrentRegime():
    Observable<RegimeDto> {
    return this.http.get<RegimeDto>(
      `${this.baseUrl}/api/refinement/current-regime`);
  }
}

### DashboardDataService

Polling service that keeps data fresh.
Components inject this, not ApiService directly.

Injectable({ providedIn: 'root' })
export class DashboardDataService
  implements OnDestroy {

  private readonly POLL_INTERVAL = 60_000;
  private destroy$ = new Subject<void>();

  private portfolioSubject =
    new BehaviorSubject<PortfolioDto | null>(null);
  private positionsSubject =
    new BehaviorSubject<PositionDto[]>([]);
  private signalsSubject =
    new BehaviorSubject<SignalGroupDto | null>(null);
  private statusSubject =
    new BehaviorSubject<StatusDto | null>(null);
  private regimeSubject =
    new BehaviorSubject<RegimeDto | null>(null);

  portfolio$ =
    this.portfolioSubject.asObservable();
  positions$ =
    this.positionsSubject.asObservable();
  signals$ =
    this.signalsSubject.asObservable();
  status$ =
    this.statusSubject.asObservable();
  regime$ =
    this.regimeSubject.asObservable();

  lastUpdated = signal<Date | null>(null);
  isLoading = signal<boolean>(false);
  error = signal<string | null>(null);

  constructor(private api: ApiService) {
    this.startPolling();
  }

  private startPolling(): void {
    interval(this.POLL_INTERVAL)
      .pipe(
        startWith(0),
        switchMap(() => this.fetchAll()),
        takeUntil(this.destroy$)
      )
      .subscribe();
  }

  private fetchAll(): Observable<void> {
    this.isLoading.set(true);
    this.error.set(null);

    return forkJoin({
      portfolio: this.api.getPortfolio(),
      positions: this.api.getPositions(),
      signals: this.api.getSignalsToday(),
      status: this.api.getStatus(),
      regime: this.api.getCurrentRegime()
    }).pipe(
      tap(data => {
        this.portfolioSubject.next(
          data.portfolio);
        this.positionsSubject.next(
          data.positions);
        this.signalsSubject.next(
          data.signals);
        this.statusSubject.next(
          data.status);
        this.regimeSubject.next(
          data.regime);
        this.lastUpdated.set(new Date());
        this.isLoading.set(false);
      }),
      catchError(err => {
        this.error.set(
          'Failed to load data. Retrying...');
        this.isLoading.set(false);
        return of(undefined as any);
      }),
      map(() => void 0)
    );
  }

  refresh(): void {
    this.fetchAll().subscribe();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }
}

### AuthService (Shell — inactive until 10c)

Injectable({ providedIn: 'root' })
export class AuthService {

  // Phase 10c will implement MSAL here.
  // For now: always authenticated.
  // Guards use this service — when 10c
  // wires it up, no guard changes needed.

  isAuthenticated = signal<boolean>(true);
  currentUser = signal<UserDto | null>(null);

  login(): void {
    // Phase 10c: redirect to Google OAuth
    console.log('Auth not yet implemented');
  }

  logout(): void {
    // Phase 10c: MSAL logout
    console.log('Auth not yet implemented');
  }
}

## App Shell

### app.component.ts

Standalone component.
Material sidenav layout.
Responsive: sidenav on desktop,
bottom nav on mobile.

Template structure:

<mat-sidenav-container>

  <mat-sidenav mode="side" opened
    class="sidenav">

    <div class="logo">
      <span>SwingTrader</span>
      <app-regime-badge />
    </div>

    <mat-nav-list>
      <a mat-list-item routerLink="/dashboard"
        routerLinkActive="active">
        <mat-icon>dashboard</mat-icon>
        Dashboard
      </a>
      <a mat-list-item routerLink="/signals"
        routerLinkActive="active">
        <mat-icon>analytics</mat-icon>
        Signals
      </a>
      <a mat-list-item routerLink="/trades"
        routerLinkActive="active">
        <mat-icon>swap_horiz</mat-icon>
        Trades
      </a>
      <a mat-list-item routerLink="/refinement"
        routerLinkActive="active">
        <mat-icon>tune</mat-icon>
        Refinement
      </a>
      <a mat-list-item routerLink="/readiness"
        routerLinkActive="active">
        <mat-icon>checklist</mat-icon>
        Readiness
      </a>
      <a mat-list-item routerLink="/settings"
        routerLinkActive="active">
        <mat-icon>settings</mat-icon>
        Settings
      </a>
    </mat-nav-list>

    <div class="sidenav-footer">
      <span class="last-updated">
        Updated {{ lastUpdated() | relativeTime }}
      </span>
    </div>

  </mat-sidenav>

  <mat-sidenav-content>
    <mat-toolbar>
      <span>{{ pageTitle() }}</span>
      <span class="spacer"></span>
      <button mat-icon-button
        (click)="data.refresh()"
        [disabled]="data.isLoading()">
        <mat-icon>refresh</mat-icon>
      </button>
    </mat-toolbar>

    @if (data.error()) {
      <app-error-card [message]="data.error()!" />
    }

    <div class="content">
      <router-outlet />
    </div>
  </mat-sidenav-content>

</mat-sidenav-container>

### app.routes.ts

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full'
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./features/dashboard/
        dashboard.component')
        .then(m => m.DashboardComponent),
    title: 'Dashboard'
  },
  {
    path: 'signals',
    loadComponent: () =>
      import('./features/signals/
        signals.component')
        .then(m => m.SignalsComponent),
    title: 'Signals'
  },
  {
    path: 'trades',
    loadComponent: () =>
      import('./features/trades/
        trades.component')
        .then(m => m.TradesComponent),
    title: 'Trades'
  },
  {
    path: 'refinement',
    loadComponent: () =>
      import('./features/refinement/
        refinement.component')
        .then(m => m.RefinementComponent),
    title: 'Refinement'
  },
  {
    path: 'readiness',
    loadComponent: () =>
      import('./features/readiness/
        readiness.component')
        .then(m => m.ReadinessComponent),
    title: 'Readiness'
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./features/settings/
        settings.component')
        .then(m => m.SettingsComponent),
    title: 'Settings'
  }
];

## Shared Components

### RegimeBadge Component

Displays current market regime.
Used in sidenav and dashboard.

@Component({
  selector: 'app-regime-badge',
  standalone: true,
  template: `
    @if (regime()) {
      <span class="regime-badge"
        [class]="regimeClass()">
        {{ regime()!.regime }}
        @if (regime()!.regime === 'Crisis') {
          <span class="pulse-dot"></span>
        }
      </span>
    }
  `,
  styles: [`
    .regime-badge {
      padding: 4px 10px;
      border-radius: 999px;
      font-size: 12px;
      font-weight: 500;
      letter-spacing: 0.05em;
    }
    .bull { background: #14532d;
            color: #22c55e; }
    .neutral { background: #713f12;
               color: #f59e0b; }
    .bear { background: #7f1d1d;
            color: #ef4444; }
    .crisis { background: #7f1d1d;
              color: #ef4444;
              animation: pulse 1s infinite; }
    @keyframes pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.5; }
    }
  `]
})
export class RegimeBadgeComponent {
  regime = input<RegimeDto | null>(null);
  regimeClass = computed(() =>
    this.regime()?.regime.toLowerCase() ?? '');
}

### StopTargetBar Component

Visual bar showing position relative
to stop loss and target.
Replaces the existing HTML/CSS bars.

@Component({
  selector: 'app-stop-target-bar',
  standalone: true,
  template: `
    <div class="bar-container">
      <div class="labels">
        <span class="stop">
          £{{ position().stopLoss | number:'1.2-2' }}
        </span>
        <span class="current"
          [class.near-stop]="position().isNearStop"
          [class.near-target]="position().isNearTarget">
          £{{ position().currentPrice | number:'1.2-2' }}
        </span>
        <span class="target">
          £{{ position().target | number:'1.2-2' }}
        </span>
      </div>
      <div class="track">
        <div class="danger-zone"
          [style.width.%]="dangerWidth()">
        </div>
        <div class="safe-zone"
          [style.width.%]="safeWidth()">
        </div>
        <div class="current-marker"
          [style.left.%]="currentPosition()">
        </div>
      </div>
    </div>
  `
})
export class StopTargetBarComponent {
  position = input.required<PositionDto>();

  dangerWidth = computed(() => {
    const p = this.position();
    const total = p.target - p.stopLoss;
    const danger = p.entryPrice - p.stopLoss;
    return (danger / total) * 100;
  });

  safeWidth = computed(() =>
    100 - this.dangerWidth());

  currentPosition = computed(() => {
    const p = this.position();
    const total = p.target - p.stopLoss;
    const current = p.currentPrice - p.stopLoss;
    return Math.max(0, Math.min(100,
      (current / total) * 100));
  });
}

### ConvictionBar Component

Horizontal bar showing conviction score
with component breakdown on hover.

@Component({
  selector: 'app-conviction-bar',
  standalone: true,
  template: `
    <div class="conviction"
      [matTooltip]="tooltipText()">
      <span class="score">
        {{ signal().convictionScore | number:'1.1-1' }}
      </span>
      <div class="bar">
        <div class="fill"
          [style.width.%]="signal().convictionScore * 10"
          [class]="fillClass()">
        </div>
      </div>
    </div>
  `
})
export class ConvictionBarComponent {
  signal = input.required<SignalDto>();

  fillClass = computed(() => {
    const score = this.signal().convictionScore;
    if (score >= 7) return 'high';
    if (score >= 5) return 'medium';
    return 'low';
  });

  tooltipText = computed(() => {
    const s = this.signal();
    return [
      `RSI: ${s.rsiScore?.toFixed(2) ?? 'n/a'}`,
      `MACD: ${s.macdScore?.toFixed(2) ?? 'n/a'}`,
      `Volume: ${s.volumeScore?.toFixed(2) ?? 'n/a'}`,
      `Sentiment: ${s.sentimentComponentScore?.toFixed(2) ?? 'n/a'}`,
      `Setup: ${s.setupQualityScore?.toFixed(2) ?? 'n/a'}`,
      `RelStrength: ${s.relativeStrengthScore?.toFixed(2) ?? 'n/a'}`,
      `PriceLevel: ${s.priceLevelScore?.toFixed(2) ?? 'n/a'}`,
      `Fundamental: ${s.fundamentalMomentumScore?.toFixed(2) ?? 'n/a'}`,
    ].join('\n');
  });
}

## Feature Pages

### Dashboard Page

dashboard.component.ts injects
DashboardDataService and displays:

Row 1 — Stat Cards (4 across, Material cards):
  Total Capital (£)
  Cash Available (£)
  Today P&L (+/- £ and %)
  30-Day Win Rate (%)

Row 2 — Two columns:
  LEFT: Open Positions
    Card per position
    StopTargetBar component
    Unrealised P&L with colour
    Days held
    ⚠️ warnings if near stop or time alert

  RIGHT: Worker Status
    List of workers with last run time
    Colour coded: green/amber/red
    Next scheduled run
    Status badge from WorkerHeartbeat

Row 3 — Today's Signals (tabs)
  [BUY (n)] [WATCH (n)] [HOLD (n)] [AVOID (n)]
  Cards per signal in active tab
  ConvictionBar component
  Setup type badge
  Key stats (RSI, Volume ratio)
  Earnings warning if applicable

Row 4 — Recent Trades (AG Grid)
  Columns: Symbol, Direction, Entry,
    Exit, P&L, P&L%, Days, Result, Date
  Sortable, filterable
  Row colour: green for wins, red for losses
  Pagination: 10 rows default

Footer — Manual Triggers
  Row of buttons:
  [▶ Research] [▶ Report] [▶ Execution]
  [▶ Monitor] [▶ Risk] [▶ Refinement]
  Each shows a spinner while running
  Shows success/error snackbar on complete

  Approval button (if RequireApproval=true
  and today not yet approved):
  [✅ Approve Today's Trades]
  Prominent, coloured differently
  Shows approved state after click

### Signals Page

Full view of today's research output.
All 25 signals, filterable and sortable.

AG Grid with columns:
  Symbol
  Company name
  Conviction (with ConvictionBar)
  Recommendation (badge)
  Setup type
  RSI
  Volume ratio
  Sentiment score
  Relative strength
  Earnings (if applicable)
  Analyst trend
  Insider activity
  Fundamental narrative (expandable)

Row click: expands to show full
fundamental narrative and all
component scores.

Filter bar:
  [All] [BUY] [WATCH] [HOLD] [AVOID]
  Search by symbol
  Filter by setup type

Export button: download as CSV

### Trades Page

Two tabs:

Tab 1 — Open Positions
  Cards (same as dashboard)
  StopTargetBar for each
  More detail than dashboard:
    Full stop/target/trailing stop info
    P&L in pounds and percent
    Days held, entry date
    Setup type that triggered entry
    Conviction score at entry
    Market regime at entry

Tab 2 — Trade History (AG Grid)
  All closed trades
  Columns: Symbol, Entry, Exit,
    Return%, Hold Days, Setup Type,
    Conviction, Regime, Outcome
  Row colour: green wins, red losses
  Sortable by any column
  Date range filter
  Summary row: win rate, avg return

Chart section below grid:
  Equity curve (line chart)
  Win/loss distribution (bar chart)
  Return by setup type (bar chart)
  These update based on date filter

### Refinement Page

Current Weights section:
  8 horizontal bars, one per component
  Shows weight as both bar and number
  Labelled with component name

Latest Suggestion section:
  If no suggestion:
    "No analysis yet"
    Progress toward minimum trades

  If shadow suggestion:
    Full analysis table
    Component findings
    Greyed Apply button
    "Shadow mode — enable
     Refinement:Active to apply"

  If live suggestion (Pending):
    Full analysis table
    Comparison bars:
      Current vs Suggested per component
      Green if increasing, red if decreasing
    Claude assessment text
    Confidence badge (Low/Medium/High)
    Trade count and period
    Market condition warning (if applicable)
    [Apply General Weights] button
    [Reject] button with note input

Suggestion History section:
  Table: Date, Trades, Confidence,
    Status (Applied/Rejected/Superseded)
  Click row to view full detail

### Readiness Page

Overall maturity badge:
  Early Stage / Developing /
  Established / Mature

Win Rate card:
  Large percentage
  Confidence interval (if sufficient data)
  "Too few trades" message if < 10
  Progress bar showing CI width

Feature Cards (one per feature):
  Traffic light status
  Criteria checklist with ✅/❌
  Assessment paragraph
  Estimated ready date range
  Action section:
    How to enable (config snippet)
    OR Live Trading confirmation flow
    OR "Requires code build" (Phase 8)

Regime Progress section:
  Bull / Neutral / Bear progress bars
  Excludes Crisis
  Bear shows market-dependent note

Trajectory table:
  Week-by-week trade count
  Win rate progression
  Speed indicators (↑↓→)
  Warning if slowing

Milestones timeline:
  Horizontal timeline
  Estimated dates as ranges
  Completed milestones marked

### Settings Page

Placeholder in this phase.
Shows one card:

"Settings will be available in a
 future update. API keys and account
 configuration will be managed here."

Fully implemented in Phase 10e.

## Auth Interceptor (Inactive Shell)

src/app/core/interceptors/auth.interceptor.ts

export const authInterceptor:
  HttpInterceptorFn = (req, next) => {

  // Phase 10c: inject MSAL token here
  // For now: pass through unchanged
  // When 10c activates, this interceptor
  // adds Authorization: Bearer {token}
  // to all API requests

  return next(req);
};

Register in app.config.ts:
  provideHttpClient(
    withInterceptors([authInterceptor])
  )

## Error Handling

### Global Error Handler

export class GlobalErrorHandler
  implements ErrorHandler {

  constructor(private snackbar: MatSnackBar) {}

  handleError(error: any): void {
    console.error(error);

    const message = error?.error?.message
      ?? error?.message
      ?? 'An unexpected error occurred';

    this.snackbar.open(message, 'Dismiss', {
      duration: 5000,
      panelClass: 'error-snackbar'
    });
  }
}

### HTTP Error Interceptor

export const errorInterceptor:
  HttpInterceptorFn = (req, next) => {

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 0) {
        // Network error / API down
        console.error('API unavailable');
      } else if (error.status === 401) {
        // Phase 10c: redirect to login
        console.log('Unauthorised');
      } else if (error.status === 500) {
        console.error('Server error', error);
      }
      return throwError(() => error);
    })
  );
};

## Environments

src/environments/environment.ts:
export const environment = {
  production: false,
  apiUrl: 'http://localhost:5001'
};

src/environments/environment.prod.ts:
export const environment = {
  production: true,
  apiUrl: ''
  // Empty = same origin as Angular app
  // API and Angular served from same Container App
};

## Pipes

### CurrencyGbpPipe
Formats decimal as £1,234.56
Handles positive/negative with colour class

### PercentSignedPipe
Formats decimal as +4.2% or -1.8%
Includes CSS class for colour

### RelativeTimePipe
Converts Date to "3 minutes ago"
"2 hours ago" etc.
Updates reactively via interval

## AG Grid Configuration

Global default column definition:

const defaultColDef: ColDef = {
  sortable: true,
  filter: true,
  resizable: true,
  suppressMovable: false,
  cellStyle: {
    color: 'var(--st-text)',
    backgroundColor: 'var(--st-card)'
  }
};

Theme: ag-theme-alpine-dark
Matches dark dashboard aesthetic.

Install:
  npm install ag-grid-community
  npm install ag-grid-angular

## Charts Configuration

Install:
  npm install chart.js
  npm install ng2-charts

Equity curve (line chart):
  X axis: trade close date
  Y axis: cumulative P&L in £
  Single dataset
  Green line, dark fill below

Win/loss distribution (bar chart):
  X axis: return % buckets
    (<-5%, -5 to -2%, -2 to 0%,
     0 to 2%, 2 to 5%, >5%)
  Y axis: count
  Green bars for positive, red for negative

Return by setup type (bar chart):
  X axis: setup type
  Y axis: average return %
  Colour by performance

## Package.json

{
  "name": "swingtrader-angular",
  "version": "1.0.0",
  "scripts": {
    "start": "ng serve",
    "build": "ng build --configuration production --output-path ../SwingTrader.Api/wwwroot",
    "build:dev": "ng build --output-path ../SwingTrader.Api/wwwroot",
    "test": "ng test --watch=false --browsers=ChromeHeadless",
    "lint": "ng lint",
    "generate-api": "openapi --input http://localhost:5001/swagger/v1/swagger.json --output src/app/core/models/generated --client angular"
  },
  "dependencies": {
    "@angular/animations": "^17.0.0",
    "@angular/cdk": "^17.0.0",
    "@angular/common": "^17.0.0",
    "@angular/compiler": "^17.0.0",
    "@angular/core": "^17.0.0",
    "@angular/forms": "^17.0.0",
    "@angular/material": "^17.0.0",
    "@angular/platform-browser": "^17.0.0",
    "@angular/router": "^17.0.0",
    "@azure/msal-angular": "^3.0.0",
    "@azure/msal-browser": "^3.0.0",
    "ag-grid-angular": "^31.0.0",
    "ag-grid-community": "^31.0.0",
    "chart.js": "^4.0.0",
    "ng2-charts": "^6.0.0",
    "rxjs": "~7.8.0",
    "tslib": "^2.3.0",
    "zone.js": "~0.14.0"
  },
  "devDependencies": {
    "@angular/cli": "^17.0.0",
    "@angular/compiler-cli": "^17.0.0",
    "openapi-typescript-codegen": "^0.25.0",
    "typescript": "~5.2.0"
  }
}

## GitHub Actions Update

Update deploy-api.yml to build Angular
before building the .NET API:

jobs:
  test-and-deploy:
    steps:
      # Angular build
      - name: Setup Node
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path:
            SwingTrader.Angular/package-lock.json

      - name: Install Angular dependencies
        run: npm ci
        working-directory: SwingTrader.Angular

      - name: Run Angular tests
        run: npm test
        working-directory: SwingTrader.Angular

      - name: Build Angular
        run: npm run build
        working-directory: SwingTrader.Angular
        # Output goes to SwingTrader.Api/wwwroot

      # .NET API build (wwwroot now populated)
      - name: Build and push API image
        run: |
          az acr build \
            --registry swingtradercr \
            --image swingtrader-api:${{ github.sha }} \
            --file SwingTrader.Api/Dockerfile .

## Deliverables

1. Angular project builds without errors:
   npm run build
   Output in SwingTrader.Api/wwwroot

2. Angular tests pass:
   npm test

3. dotnet test still passes:
   No .NET tests broken by frontend changes

4. Dashboard loads in browser:
   https://{container-app-url}/
   Angular app loads (not old HTML)
   Portfolio cards show real data
   Open positions visible

5. All routes work:
   /dashboard, /signals, /trades,
   /refinement, /readiness, /settings
   No 404s, Angular Router handles navigation

6. Data polling works:
   Data refreshes every 60 seconds
   Last updated timestamp updates
   Refresh button triggers immediate reload

7. Open positions show stop/target bars:
   Visual bar renders correctly
   Current price marker in correct position
   Near-stop warning shows when applicable

8. AG Grid tables render:
   Trades table sortable and filterable
   Signals table with all columns

9. Charts render:
   Equity curve visible on Trades page
   (empty state if no closed trades yet)

10. Manual trigger buttons work:
    Click Run Research
    Spinner shows while running
    Success snackbar on completion
    Data refreshes after completion

11. Regime badge visible and correct:
    Shows current regime
    Correct colour
    Updates with polling

12. GitHub Actions:
    Angular build included in pipeline
    Full deploy on push to main works

13. Mobile responsive:
    Dashboard readable on phone
    Cards stack vertically on small screen
    Navigation accessible

14. README updated:
    Angular development setup
    How to run Angular dev server
    How to regenerate API types

## What Phase 10b Does NOT Include
  No authentication (still open access)
  No per-user data (Phase 10c)
  No settings page implementation (10e)
  No Google Sign-On (10c)
  No per-user key management (10d)
  All existing API endpoints unchanged
  Single user data as before
