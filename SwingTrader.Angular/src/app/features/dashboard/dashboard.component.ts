import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AgGridAngular } from 'ag-grid-angular';
import { ColDef } from 'ag-grid-community';
import { DashboardDataService } from '../../core/services/dashboard-data.service';
import { ApiService } from '../../core/services/api.service';
import { CurrencyGbpPipe } from '../../shared/pipes/currency-gbp.pipe';
import { PercentSignedPipe } from '../../shared/pipes/percent-signed.pipe';
import { StopTargetBarComponent } from '../../shared/components/stop-target-bar/stop-target-bar.component';
import { ConvictionBarComponent } from '../../shared/components/conviction-bar/conviction-bar.component';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { defaultColDef } from '../../shared/ag-grid-defaults';
import { SignalDto, TradeDto } from '../../core/models/dtos';

const AGENTS = ['Research', 'Report', 'Execution', 'Monitor', 'Risk', 'Refinement'] as const;

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
  data = inject(DashboardDataService);

  portfolio = toSignal(this.data.portfolio$, { initialValue: null });
  positions = toSignal(this.data.positions$, { initialValue: [] });
  signals = toSignal(this.data.signals$, { initialValue: null });
  status = toSignal(this.data.status$, { initialValue: null });

  recentTrades = signal<TradeDto[]>([]);
  runningAgent = signal<string | null>(null);

  agents = AGENTS;

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
      error: () => {
        this.runningAgent.set(null);
        this.snackbar.open(`${agent} failed to run`, 'Dismiss', { duration: 4000 });
      },
    });
  }
}
