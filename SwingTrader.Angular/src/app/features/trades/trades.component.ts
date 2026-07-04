import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatTabsModule } from '@angular/material/tabs';
import { MatCardModule } from '@angular/material/card';
import { BaseChartDirective } from 'ng2-charts';
import { ChartConfiguration } from 'chart.js';
import { DashboardDataService } from '../../core/services/dashboard-data.service';
import { ApiService } from '../../core/services/api.service';
import { OpenPositionsComponent } from './open-positions/open-positions.component';
import { TradeHistoryComponent } from './trade-history/trade-history.component';
import { TradeDto } from '../../core/models/dtos';

@Component({
  selector: 'app-trades',
  standalone: true,
  imports: [
    CommonModule,
    MatTabsModule,
    MatCardModule,
    BaseChartDirective,
    OpenPositionsComponent,
    TradeHistoryComponent,
  ],
  templateUrl: './trades.component.html',
  styleUrl: './trades.component.scss',
})
export class TradesComponent {
  private api = inject(ApiService);
  data = inject(DashboardDataService);

  positions = toSignal(this.data.positions$, { initialValue: [] });
  closedTrades = signal<TradeDto[]>([]);

  constructor() {
    this.api.getRecentTrades(365).subscribe({
      next: (trades) => this.closedTrades.set(trades.filter((t) => t.status !== 'Open')),
      error: () => this.closedTrades.set([]),
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
