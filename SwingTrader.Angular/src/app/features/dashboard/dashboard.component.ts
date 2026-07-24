import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/components/confirm-dialog/confirm-dialog.component';
import { AgGridAngular } from 'ag-grid-angular';
import { ColDef } from 'ag-grid-community';
import { DashboardDataService } from '../../core/services/dashboard-data.service';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { CurrencyGbpPipe } from '../../shared/pipes/currency-gbp.pipe';
import { PercentSignedPipe } from '../../shared/pipes/percent-signed.pipe';
import { StopTargetBarComponent } from '../../shared/components/stop-target-bar/stop-target-bar.component';
import { ConvictionBarComponent } from '../../shared/components/conviction-bar/conviction-bar.component';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { defaultColDef } from '../../shared/ag-grid-defaults';
import {
  RiskProfileDto,
  MarketStatusDto, ActivityLogDto, NextRunDto, PositionDto, SignalDto, TradeDto, TradingConfigDto } from '../../core/models/dtos';
import { readTabIndexFromRoute, writeTabIndexToRoute } from '../../shared/utils/tab-route.util';
import { errorMessage } from '../../shared/utils/error-message.util';

const SIGNAL_TAB_NAMES = ['buy', 'watch', 'hold', 'avoid'] as const;

const AGENTS = ['Research', 'Watchlist', 'Report', 'Execution', 'Monitor', 'Refinement'] as const;

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    MatCardModule,
    MatTabsModule,
    MatButtonModule,
    MatIconModule,
    AgGridAngular,
    CurrencyGbpPipe,
    PercentSignedPipe,
    StopTargetBarComponent,
    ConvictionBarComponent,
    LoadingSpinnerComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  private dialog = inject(MatDialog);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private titleService = inject(Title);
  auth = inject(AuthService);
  data = inject(DashboardDataService);

  portfolio = toSignal(this.data.portfolio$, { initialValue: null });
  regime = toSignal(this.data.regime$, { initialValue: null });
  positions = toSignal(this.data.positions$, { initialValue: [] });
  signals = toSignal(this.data.signals$, { initialValue: null });
  status = toSignal(this.data.status$, { initialValue: null });

  recentTrades = signal<TradeDto[]>([]);
  runningAgent = signal<string | null>(null);
  accountSettings = signal<(TradingConfigDto & { globalRefinementOptIn: boolean }) | null>(null);
  nextRuns = signal<NextRunDto[]>([]);

  agents = AGENTS;

  closingPosition = signal<number | null>(null);

  // Open positions are what the user actually comes for, so they get the
  // full row; the activity log lives behind a right-edge tab that animates
  // out to a 50/50 split on demand. Choice persists across visits.
  activityOpen = signal(localStorage.getItem('dashboard.activityOpen') === '1');

  toggleActivity(): void {
    this.activityOpen.update((v) => !v);
    localStorage.setItem('dashboard.activityOpen', this.activityOpen() ? '1' : '0');
  }

  // Same flow as the Trades page's open-positions card: confirm, then place
  // a REAL market sell in Trading212 via the monitor's exit path.
  closePositionEarly(position: PositionDto): void {
    const ref = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '440px',
      data: {
        title: `Close ${position.symbol} early?`,
        message:
          `This places a REAL market sell order in Trading212 for ` +
          `${position.quantity} share(s) of ${position.symbol} right now.\n\n` +
          `Estimated P&L at the current price: £${position.unrealisedPnl.toFixed(2)} ` +
          `(${position.unrealisedPnlPercent.toFixed(1)}%). The exact figure depends on the fill.`,
        cancelLabel: 'Cancel',
        confirmLabel: 'Sell at market',
        confirmColor: 'warn',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.closingPosition.set(position.id);
      this.api.closePositionEarly(position.id).subscribe({
        next: (r) => {
          this.closingPosition.set(null);
          this.snackbar.open(
            `${r.symbol} sold at market — est. P&L £${(r.realizedPnl ?? 0).toFixed(2)} (final figure after fill reconciliation)`,
            'Dismiss', { duration: 5000 });
          this.data.refresh();
        },
        error: (err) => {
          this.closingPosition.set(null);
          this.snackbar.open(errorMessage(err, 'Sell order failed — the position is unchanged.'), 'Dismiss', { duration: 5000 });
        },
      });
    });
  }

  // Any worker that failed on its most recent run flips the overall
  // system-health capsule red - a quick "is anything broken" signal
  // without having to scan the full Worker Status panel.
  // Latest result per worker job — if any failed, the health capsule goes red.
  // Monitor and non-WorkerRun entries are excluded (user actions don't affect system health).
  isOwner = computed(() => this.accountSettings()?.role === 'Owner');

  systemHealthy = computed(() => {
    const runs = this.status()?.runs ?? [];
    const seen = new Set<string>();
    for (const r of runs) {
      if (r.category !== 'WorkerRun' || r.title === 'Monitor') continue;
      if (seen.has(r.title)) continue;
      seen.add(r.title);
      if (r.result === 'Failed') return false;
    }
    return true;
  });

  // Jobs column: all non-Monitor activity (worker runs + user actions + system events)
  jobActivity = computed(() =>
    (this.status()?.runs ?? []).filter(r => !(r.category === 'WorkerRun' && r.title === 'Monitor')),
  );

  // Monitor column: monitor worker runs only
  monitorRuns = computed(() =>
    (this.status()?.runs ?? []).filter(r => r.category === 'WorkerRun' && r.title === 'Monitor'),
  );

  marketStatus = signal<MarketStatusDto | null>(null);

  private loadMarketStatus(): void {
    this.api.getMarketStatus().subscribe({
      next: (m) => this.marketStatus.set(m),
      error: () => {},
    });
  }

  // Market open/closed capsule. Shows both the viewer's local time and ET,
  // since owners are UK-based but the session is New York's.
  marketCapsule = computed(() => {
    const m = this.marketStatus();
    if (!m) return null;
    const at = new Date(m.changesAtUtc);
    const local = at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const et = at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', timeZone: 'America/New_York' });
    if (m.isOpen) return { open: true, label: `Market open \u00b7 closes ${local} (${et} ET)` };
    const sameDay = at.toDateString() === new Date().toDateString();
    const day = sameDay ? '' : at.toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' }) + ' \u00b7 ';
    return { open: false, label: `Market closed \u00b7 opens ${day}${local} (${et} ET)` };
  });

  // The ACTIVE regime risk book (no regime param = the live one) - feeds the
  // paused-entries capsule so a regime autopause is visible on the dashboard,
  // not just in Settings.
  activeRiskBook = signal<RiskProfileDto | null>(null);

  private loadActiveRiskBook(): void {
    this.api.getRiskProfile().subscribe({
      next: (b) => this.activeRiskBook.set(b),
      error: () => {},
    });
  }

  // Paused-entries capsule for the current mode. Null when not paused. Red for
  // a circuit-breaker auto-pause, amber for a manual pause or the active
  // regime book's autopause; the tooltip spells out why and where to change
  // it. Gives confidence entries really are paused without opening Settings.
  pauseCapsule = computed(() => {
    const s = this.accountSettings();
    // Regime autopause: the ACTIVE book pauses new entries even without a
    // manual/global pause. Manual + circuit-breaker states below win the
    // label when both apply (they're the more deliberate signal).
    if (!s?.executionPaused) {
      const book = this.activeRiskBook();
      if (book?.autopauseTrading) {
        return {
          auto: false,
          label: `\u23f8 New entries paused \u00b7 ${book.regime} regime`,
          title: `The active ${book.regime} risk book has autopause on: no new entries while this regime persists. Exits still run. Change it in Settings \u203a Risk Management.`,
        };
      }
      return null;
    }
    if (!s.executionPaused) return null;
    const auto = s.executionPauseReason === 'CircuitBreaker';
    const since = s.executionPausedAt ? new Date(s.executionPausedAt) : null;
    const sinceText = since ? ` · since ${since.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}` : '';
    // Monitor-engaged regime autopause: name the regime rather than showing
    // the generic manual-pause label.
    if (s.executionPauseReason === 'RegimeAutopause') {
      const regime = this.activeRiskBook()?.regime ?? 'current';
      return {
        auto: false,
        label: `⏸ New entries paused · ${regime} regime`,
        title: `${s.tradingMode} entries auto-paused${sinceText} — the ${regime} risk book has autopause on. Exits still run; entries resume automatically when the regime permits. Change it in Settings › Risk Management.`,
      };
    }
    return auto
      ? {
          auto,
          label: '⏸ Auto-paused · daily loss limit',
          title: `${s.tradingMode} entries auto-paused by the circuit breaker${sinceText}. Exits still run. Resume in Settings › Trading.`,
        }
      : {
          auto,
          label: `⏸ ${s.tradingMode} entries paused`,
          title: `New ${s.tradingMode} entries paused${sinceText}. Exits still run. Resume in Settings › Trading.`,
        };
  });

  activeSignals = computed<SignalDto[]>(() => {
    const g = this.signals();
    if (!g) return [];
    switch (this.activeTabIndex()) {
      case 0:
        return g.buy;
      case 1:
        return g.watch;
      case 2:
        return g.hold;
      default:
        return g.avoid;
    }
  });

  activeTabIndex = signal(0);

  defaultColDef = defaultColDef;

  tradeColumnDefs: ColDef<TradeDto>[] = [
    {
      colId: 'symbol',
      headerName: 'Symbol',
      // "Apple Inc (AAPL)" - full name then ticker; bare ticker for older
      // trades with no stored company name.
      valueGetter: (p) => (p.data?.companyName ? `${p.data.companyName} (${p.data.symbol})` : (p.data?.symbol ?? '')),
    },
    { field: 'direction', headerName: 'Direction' },
    // Share price is the instrument's own per-share price (USD for US-listed
    // stocks) - Real Money is the actual £ that left/entered the account for
    // that leg, from T212's own reported fill (post FX-conversion/fees).
    { field: 'entryPrice', headerName: 'Share Price Entry', valueFormatter: (p) => `$${p.value?.toFixed(2)}` },
    {
      field: 'exitPrice',
      headerName: 'Share Price Exit',
      valueFormatter: (p) => (p.value != null ? `$${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'entryValueGbp',
      headerName: 'Real Money Entry',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'exitValueGbp',
      headerName: 'Real Money Exit',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'feesGbp',
      headerName: 'Fees',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    { field: 'realizedPnl', headerName: 'P&L', valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-') },
    {
      field: 'realizedPnlPercent',
      headerName: 'P&L %',
      valueFormatter: (p) => (p.value != null ? `${p.value.toFixed(1)}%` : '-'),
    },
    { field: 'daysHeld', headerName: 'Days' },
    { field: 'status', headerName: 'Result' },
    { field: 'closedAt', headerName: 'Date' },
  ];

  constructor() {
    this.loadMarketStatus();
    this.loadRecentTrades();
    this.loadAccountSettings();
    this.loadActiveRiskBook();
    this.loadNextRuns();
    this.activeTabIndex.set(readTabIndexFromRoute(this.route, SIGNAL_TAB_NAMES));
  }

  heldLabel(position: PositionDto): string {
    if (position.daysHeld >= 1) return `${position.daysHeld} ${position.daysHeld === 1 ? 'day' : 'days'} held`;
    const hours = Math.floor((Date.now() - new Date(position.entryDate).getTime()) / 3_600_000);
    return `${hours} hour${hours === 1 ? '' : 's'} held`;
  }

  phaseLabel(position: PositionDto): string {
    switch (position.phase) {
      case 'Confirmed':
        return `Day ${position.daysHeld} · Confirmed`;
      case 'Exiting':
        return `Day ${position.daysHeld} · Momentum exit in progress`;
      default:
        return `Day ${position.daysHeld} · Probation`;
    }
  }

  onSignalTabChange(index: number): void {
    this.activeTabIndex.set(index);
    writeTabIndexToRoute(this.router, this.route, SIGNAL_TAB_NAMES, index);
  }

  private loadNextRuns(): void {
    this.api.getNextRuns().subscribe({
      next: (runs) => this.nextRuns.set(runs),
      error: () => this.nextRuns.set([]),
    });
  }

  // The API's nextRunLabel is formatted in ET (the market's own timezone) -
  // build the label from the raw UTC instant instead so it displays in the
  // viewer's local time via the `date` pipe's default timezone.
  nextRunLabelFor(agent: string): Date | null {
    const match = this.nextRuns().find((r) => r.jobType.toLowerCase() === agent.toLowerCase());
    return match ? new Date(match.nextRunAtUtc) : null;
  }

  private loadAccountSettings(): void {
    this.api.getAccountSettings().subscribe({
      next: (settings) => this.accountSettings.set(settings),
      error: () => this.accountSettings.set(null),
    });
  }

  private loadRecentTrades(): void {
    this.api.getRecentTrades(30).subscribe({
      next: (trades) => this.recentTrades.set(trades),
      error: () => this.recentTrades.set([]),
    });
  }

  rowClass(params: { data?: TradeDto }): string {
    if (!params.data?.realizedPnl) return '';
    return params.data.realizedPnl >= 0 ? 'row-win' : 'row-loss';
  }

  runAgent(agent: string): void {
    this.runningAgent.set(agent);
    this.api.runAgent(agent.toLowerCase()).subscribe({
      next: (result) => {
        this.runningAgent.set(null);
        this.snackbar.open(result.message ?? `${agent} started`, 'Dismiss', { duration: 4000 });
        this.data.refresh();
        this.loadRecentTrades();
      },
      error: (err) => {
        this.runningAgent.set(null);
        this.snackbar.open(errorMessage(err, `${agent} failed to run`), 'Dismiss', { duration: 4000 });
      },
    });
  }
}
