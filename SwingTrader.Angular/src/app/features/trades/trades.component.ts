import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { MatTabsModule } from '@angular/material/tabs';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { BaseChartDirective } from 'ng2-charts';
import { ChartConfiguration } from 'chart.js';
import { DashboardDataService } from '../../core/services/dashboard-data.service';
import { ApiService } from '../../core/services/api.service';
import { OpenPositionsComponent } from './open-positions/open-positions.component';
import { TradeHistoryComponent } from './trade-history/trade-history.component';
import { TradeApprovalCandidateDto, TradeApprovalDto, TradeDto } from '../../core/models/dtos';
import { ConfirmApproveDialogComponent } from '../../shared/components/confirm-approve-dialog/confirm-approve-dialog.component';
import { readTabIndexFromRoute, writeTabIndexToRoute } from '../../shared/utils/tab-route.util';

// Query-param-driven tab selection (?tab=approvals) so links/emails can
// deep-link straight to a specific tab instead of always landing on the
// first one.
const TAB_NAMES = ['positions', 'history', 'approvals'] as const;

@Component({
  selector: 'app-trades',
  standalone: true,
  imports: [
    CommonModule,
    MatTabsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    BaseChartDirective,
    OpenPositionsComponent,
    TradeHistoryComponent,
  ],
  templateUrl: './trades.component.html',
  styleUrl: './trades.component.scss',
})
export class TradesComponent {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);
  private titleService = inject(Title);
  data = inject(DashboardDataService);

  positions = toSignal(this.data.positions$, { initialValue: [] });
  closedTrades = signal<TradeDto[]>([]);
  approvals = signal<TradeApprovalDto[]>([]);
  isOwner = signal(false);

  selectedTabIndex = signal(0);

  constructor() {
    this.api.getAccountSettings().subscribe({
      next: (s) => this.isOwner.set(s.role === 'Owner'),
      error: () => {},
    });
    this.api.getRecentTrades(365).subscribe({
      next: (trades) => this.closedTrades.set(trades.filter((t) => t.status !== 'Open')),
      error: () => this.closedTrades.set([]),
    });
    this.loadApprovals();
    this.selectedTabIndex.set(readTabIndexFromRoute(this.route, TAB_NAMES));
  }

  onTabChange(index: number): void {
    this.selectedTabIndex.set(index);
    writeTabIndexToRoute(this.router, this.route, TAB_NAMES, index, this.titleService, 'Trades');
  }

  private loadApprovals(): void {
    this.api.getApprovals().subscribe({
      next: (approvals) => this.approvals.set(approvals),
      error: () => this.approvals.set([]),
    });
  }

  isToday(tradeDate: string): boolean {
    const today = new Date();
    const d = new Date(tradeDate);
    return d.getUTCFullYear() === today.getFullYear() &&
           d.getUTCMonth() === today.getMonth() &&
           d.getUTCDate() === today.getDate();
  }

  approveTrade(approval: TradeApprovalDto): void {
    const dialogRef = this.dialog.open(ConfirmApproveDialogComponent, {
      data: { tradeDate: approval.tradeDate },
      width: '420px',
    });

    dialogRef.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;

      this.api.approveTradeApproval(approval.id).subscribe({
        next: () => {
          this.snackbar.open('Trades approved', 'Dismiss', { duration: 3000 });
          this.loadApprovals();
        },
        error: () => this.snackbar.open('Failed to approve — try again.', 'Dismiss', { duration: 4000 }),
      });
    });
  }

  equityCurveData = computed<ChartConfiguration<'line'>['data']>(() => {
    const trades = [...this.closedTrades()].sort(
      (a, b) => new Date(a.closedAt ?? 0).getTime() - new Date(b.closedAt ?? 0).getTime(),
    );
    let cumulative = 0;
    const points = trades.map((t) => {
      cumulative += t.realizedPnl ?? 0;
      return cumulative;
    });
    return {
      labels: trades.map((t) => (t.closedAt ? new Date(t.closedAt).toLocaleDateString() : '')),
      datasets: [
        {
          data: points,
          label: 'Cumulative P&L (£)',
          borderColor: '#22c55e',
          backgroundColor: 'rgba(34,197,94,0.15)',
          fill: true,
          tension: 0.2,
        },
      ],
    };
  });

  distributionData = computed<ChartConfiguration<'bar'>['data']>(() => {
    const buckets = [
      { label: '<-5%', min: -Infinity, max: -5 },
      { label: '-5 to -2%', min: -5, max: -2 },
      { label: '-2 to 0%', min: -2, max: 0 },
      { label: '0 to 2%', min: 0, max: 2 },
      { label: '2 to 5%', min: 2, max: 5 },
      { label: '>5%', min: 5, max: Infinity },
    ];
    const counts = buckets.map(
      (b) =>
        this.closedTrades().filter(
          (t) => (t.realizedPnlPercent ?? 0) >= b.min && (t.realizedPnlPercent ?? 0) < b.max,
        ).length,
    );
    return {
      labels: buckets.map((b) => b.label),
      datasets: [
        {
          data: counts,
          label: 'Trades',
          backgroundColor: buckets.map((b) => (b.min >= 0 ? '#22c55e' : '#ef4444')),
        },
      ],
    };
  });

  setupTypeData = computed<ChartConfiguration<'bar'>['data']>(() => {
    const bySetup = new Map<string, number[]>();
    for (const t of this.closedTrades()) {
      const arr = bySetup.get(t.setupType) ?? [];
      arr.push(t.realizedPnlPercent ?? 0);
      bySetup.set(t.setupType, arr);
    }
    const labels = [...bySetup.keys()];
    const averages = labels.map((label) => {
      const arr = bySetup.get(label)!;
      return arr.reduce((a, b) => a + b, 0) / arr.length;
    });
    return {
      labels,
      datasets: [
        {
          data: averages,
          label: 'Avg Return %',
          backgroundColor: averages.map((v) => (v >= 0 ? '#22c55e' : '#ef4444')),
        },
      ],
    };
  });

  chartOptions: ChartConfiguration['options'] = {
    responsive: true,
    plugins: { legend: { labels: { color: '#f1f5f9' } } },
    scales: {
      x: { ticks: { color: '#94a3b8' }, grid: { color: '#334155' } },
      y: { ticks: { color: '#94a3b8' }, grid: { color: '#334155' } },
    },
  };
}
