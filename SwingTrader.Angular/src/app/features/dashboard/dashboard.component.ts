import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
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
import { ActivityLogDto, NextRunDto, PositionDto, SignalDto, TradeDto, TradingConfigDto } from '../../core/models/dtos';
import { readTabIndexFromRoute, writeTabIndexToRoute } from '../../shared/utils/tab-route.util';
import { errorMessage } from '../../shared/utils/error-message.util';

const SIGNAL_TAB_NAMES = ['buy', 'watch', 'hold', 'avoid'] as const;

const AGENTS = ['Research', 'Watchlist', 'Report', 'Execution', 'Monitor', 'Risk', 'Refinement'] as const;

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
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private titleService = inject(Title);
  auth = inject(AuthService);
  data = inject(DashboardDataService);

  portfolio = toSignal(this.data.portfolio$, { initialValue: null });
  positions = toSignal(this.data.positions$, { initialValue: [] });
  signals = toSignal(this.data.signals$, { initialValue: null });
  status = toSignal(this.data.status$, { initialValue: null });

  recentTrades = signal<TradeDto[]>([]);
  runningAgent = signal<string | null>(null);
  accountSettings = signal<(TradingConfigDto & { globalRefinementOptIn: boolean }) | null>(null);
  nextRuns = signal<NextRunDto[]>([]);

  agents = AGENTS;

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
    { field: 'symbol', headerName: 'Symbol' },
    { field: 'direction', headerName: 'Direction' },
    { field: 'entryPrice', headerName: 'Entry', valueFormatter: (p) => `£${p.value?.toFixed(2)}` },
    {
      field: 'exitPrice',
      headerName: 'Exit',
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
    this.loadRecentTrades();
    this.loadAccountSettings();
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
    writeTabIndexToRoute(this.router, this.route, SIGNAL_TAB_NAMES, index, this.titleService, 'Dashboard');
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
        this.snackbar.open(result.message ?? `${agent} completed`, 'Dismiss', { duration: 4000 });
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
