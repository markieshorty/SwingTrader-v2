import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, of } from 'rxjs';
import { ApiService } from '../../core/services/api.service';
import { DashboardDataService } from '../../core/services/dashboard-data.service';
import { SignalCardComponent } from './signal-card/signal-card.component';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { SignalDto, SignalGroupDto } from '../../core/models/dtos';

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
    MatSlideToggleModule,
    MatTooltipModule,
    SignalCardComponent,
    LoadingSpinnerComponent,
  ],
  templateUrl: './signals.component.html',
  styleUrl: './signals.component.scss',
})
export class SignalsComponent {
  private api = inject(ApiService);
  data = inject(DashboardDataService);
  todaysSignalsGroup = toSignal(this.data.signals$, { initialValue: null });

  // Default off: the page shows today's signals only unless the user
  // explicitly asks to see history. Research/Execution never act on
  // anything but today's signals - this toggle is purely a read-only view.
  showHistoric = signal(false);
  private historicLoading = signal(false);
  private historicGroup = signal<SignalGroupDto | null>(null);

  signalsGroup = computed<SignalGroupDto | null>(() =>
    this.showHistoric() ? this.historicGroup() : this.todaysSignalsGroup(),
  );

  isLoading = computed(() => this.showHistoric() ? this.historicLoading() && !this.historicGroup() : !this.todaysSignalsGroup());

  filter = signal<FilterKey>('All');
  search = signal('');

  toggleHistoric(checked: boolean): void {
    this.showHistoric.set(checked);
    if (checked && !this.historicGroup()) {
      this.historicLoading.set(true);
      this.api
        .getSignalsHistory()
        .pipe(catchError(() => of(null)))
        .subscribe((g) => {
          this.historicGroup.set(g);
          this.historicLoading.set(false);
        });
    }
  }

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
    const header = 'Date,Symbol,CompanyName,Conviction,Recommendation,SetupType,RSI,VolumeRatio\n';
    const body = rows
      .map(
        (s) =>
          `${s.signalDate},${s.symbol},${s.companyName},${s.convictionScore ?? ''},${s.recommendation},${s.setupType},${s.rsi14 ?? ''},${s.volumeRatio ?? ''}`,
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
