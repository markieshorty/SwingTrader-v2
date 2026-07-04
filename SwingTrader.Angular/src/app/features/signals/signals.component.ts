import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { DashboardDataService } from '../../core/services/dashboard-data.service';
import { SignalCardComponent } from './signal-card/signal-card.component';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { SignalDto } from '../../core/models/dtos';

type FilterKey = 'All' | 'BUY' | 'WATCH' | 'HOLD' | 'AVOID';

@Component({
  selector: 'app-signals',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    SignalCardComponent,
    LoadingSpinnerComponent,
  ],
  templateUrl: './signals.component.html',
  styleUrl: './signals.component.scss',
})
export class SignalsComponent {
  data = inject(DashboardDataService);
  signalsGroup = toSignal(this.data.signals$, { initialValue: null });

  filter = signal<FilterKey>('All');
  search = signal('');

  allSignals = computed<SignalDto[]>(() => {
    const g = this.signalsGroup();
    if (!g) return [];
    return [...g.buy, ...g.watch, ...g.hold, ...g.avoid];
  });

  filteredSignals = computed<SignalDto[]>(() => {
    let list = this.allSignals();
    const f = this.filter();
    if (f !== 'All') {
      list = list.filter((s) => s.recommendation.toUpperCase() === f);
    }
    const term = this.search().trim().toLowerCase();
    if (term) {
      list = list.filter((s) => s.symbol.toLowerCase().includes(term));
    }
    return list;
  });

  setFilter(f: FilterKey): void {
    this.filter.set(f);
  }

  updateSearch(value: string): void {
    this.search.set(value);
  }

  exportCsv(): void {
    const rows = this.filteredSignals();
    const header = 'Symbol,CompanyName,Conviction,Recommendation,SetupType,RSI,VolumeRatio\n';
    const body = rows
      .map(
        (s) =>
          `${s.symbol},${s.companyName},${s.convictionScore ?? ''},${s.recommendation},${s.setupType},${s.rsi14 ?? ''},${s.volumeRatio ?? ''}`,
      )
      .join('\n');
    const blob = new Blob([header + body], { type: 'text/csv' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'signals.csv';
    a.click();
    URL.revokeObjectURL(url);
  }
}
