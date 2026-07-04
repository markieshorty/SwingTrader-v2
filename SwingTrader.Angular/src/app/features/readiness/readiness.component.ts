import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { ApiService } from '../../core/services/api.service';
import { FeatureCardComponent } from './feature-card/feature-card.component';
import { MilestoneTimelineComponent } from './milestone-timeline/milestone-timeline.component';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { ReadinessReportDto } from '../../core/models/dtos';

@Component({
  selector: 'app-readiness',
  standalone: true,
  imports: [CommonModule, MatCardModule, FeatureCardComponent, MilestoneTimelineComponent, LoadingSpinnerComponent],
  templateUrl: './readiness.component.html',
  styleUrl: './readiness.component.scss',
})
export class ReadinessComponent {
  private api = inject(ApiService);
  report = signal<ReadinessReportDto | null>(null);
  loaded = signal(false);

  constructor() {
    this.api.getReadiness().subscribe({
      next: (report) => {
        this.report.set(report);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(true),
    });
  }
}
