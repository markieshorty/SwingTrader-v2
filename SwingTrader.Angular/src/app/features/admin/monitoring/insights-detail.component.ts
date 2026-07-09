import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ApiService } from '../../../core/services/api.service';
import { InsightsDetailSectionDto } from '../../../core/models/dtos';

const TITLES: Record<string, string> = {
  exceptions: 'Server exceptions',
  dependencies: 'Dependency failures',
  claude429: 'Claude rate-limits (429)',
};

@Component({
  selector: 'app-insights-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatIconModule, MatButtonModule, MatProgressSpinnerModule],
  templateUrl: './insights-detail.component.html',
  styleUrl: './monitoring.component.scss',
})
export class InsightsDetailComponent {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);

  kind = toSignal(this.route.paramMap.pipe(map((p) => p.get('kind') ?? 'exceptions')), {
    initialValue: 'exceptions',
  });
  heading = computed(() => TITLES[this.kind()] ?? 'App Insights events');

  data = signal<InsightsDetailSectionDto | null>(null);
  loading = signal(true);
  error = signal(false);

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api.getMonitoringInsightsDetail(this.kind()).subscribe({
      next: (d) => {
        this.data.set(d);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}
