import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatIconModule } from '@angular/material/icon';
import { ApiService } from '../../../core/services/api.service';
import { AdminUserOverviewDto, PositionDto, SignalDto } from '../../../core/models/dtos';
import { StopTargetBarComponent } from '../../../shared/components/stop-target-bar/stop-target-bar.component';
import { ConvictionBarComponent } from '../../../shared/components/conviction-bar/conviction-bar.component';
import { TradeHistoryComponent } from '../../trades/trade-history/trade-history.component';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { CurrencyGbpPipe } from '../../../shared/pipes/currency-gbp.pipe';
import { PercentSignedPipe } from '../../../shared/pipes/percent-signed.pipe';

// Read-only admin view of a single user's account — the same portfolio /
// positions / signals / trades the owner sees on their own dashboard (fetched
// via /api/admin/users/{userId}/overview, which reuses the shared
// AccountViewService), plus their watchlists inline. Reuses the dashboard's
// presentational components so it looks and reads identically.
@Component({
  selector: 'app-admin-user-view',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatCardModule,
    MatTabsModule,
    MatIconModule,
    StopTargetBarComponent,
    ConvictionBarComponent,
    TradeHistoryComponent,
    LoadingSpinnerComponent,
    CurrencyGbpPipe,
    PercentSignedPipe,
  ],
  templateUrl: './admin-user-view.component.html',
  styleUrl: './admin-user-view.component.scss',
})
export class AdminUserViewComponent {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);

  overview = signal<AdminUserOverviewDto | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);

  activeTabIndex = signal(0);
  activeSignals = computed<SignalDto[]>(() => {
    const g = this.overview()?.signals;
    if (!g) return [];
    switch (this.activeTabIndex()) {
      case 0: return g.buy;
      case 1: return g.watch;
      case 2: return g.hold;
      default: return g.avoid;
    }
  });

  constructor() {
    const userId = this.route.snapshot.paramMap.get('userId');
    if (!userId) {
      this.error.set('No user specified.');
      this.loading.set(false);
      return;
    }
    this.api.getAdminUserOverview(userId).subscribe({
      next: (data) => {
        this.overview.set(data);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load this user’s account.');
        this.loading.set(false);
      },
    });
  }

  heldLabel(position: PositionDto): string {
    if (position.daysHeld >= 1) return `${position.daysHeld} ${position.daysHeld === 1 ? 'day' : 'days'} held`;
    const hours = Math.floor((Date.now() - new Date(position.entryDate).getTime()) / 3_600_000);
    return `${hours} hour${hours === 1 ? '' : 's'} held`;
  }

  phaseLabel(position: PositionDto): string {
    switch (position.phase) {
      case 'Confirmed': return `Day ${position.daysHeld} · Confirmed`;
      case 'Exiting': return `Day ${position.daysHeld} · Momentum exit in progress`;
      default: return `Day ${position.daysHeld} · Probation`;
    }
  }
}
