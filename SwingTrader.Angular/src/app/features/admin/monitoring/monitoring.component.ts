import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ApiService } from '../../../core/services/api.service';
import { MonitoringDashboardDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-monitoring',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatIconModule, MatButtonModule, MatProgressSpinnerModule],
  templateUrl: './monitoring.component.html',
  styleUrl: './monitoring.component.scss',
})
export class MonitoringComponent {
  private api = inject(ApiService);

  data = signal<MonitoringDashboardDto | null>(null);
  loading = signal(true);
  error = signal(false);

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api.getMonitoringDashboard().subscribe({
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

  // Human-readable "last run X ago" from minutes-since-heartbeat.
  relativeAge(minutes: number): string {
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${Math.round(minutes)}m ago`;
    const hours = minutes / 60;
    if (hours < 48) return `${Math.round(hours)}h ago`;
    return `${Math.round(hours / 24)}d ago`;
  }

  // Map a worker/event result string to a status colour class.
  resultClass(result: string): string {
    switch (result) {
      case 'Success':
        return 'ok';
      case 'Warning':
        return 'warn';
      case 'Failed':
        return 'bad';
      default:
        return 'muted';
    }
  }
}
