import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterModule } from '@angular/router';
import { ApiService } from '../../core/services/api.service';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import {
  FilingDeltaRowDto,
  FilingsIntelligenceDto,
  FunnelShadowDto,
  SecondHopIntelligenceDto,
} from '../../core/models/dtos';

// Read-only evidence page (docs/intelligence-page-plan): the funnel's shadow
// record, filing-language deltas, and second-hop transmissions. Nothing here
// mutates anything - it exists so the review-before-trust decisions (flipping
// FunnelEnabled, raising FD/SH weights) are made with the evidence in view.
@Component({
  selector: 'app-intelligence',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    MatIconModule,
    MatTabsModule,
    MatTooltipModule,
    RouterModule,
    LoadingSpinnerComponent,
  ],
  templateUrl: './intelligence.component.html',
  styleUrl: './intelligence.component.scss',
})
export class IntelligenceComponent {
  private api = inject(ApiService);

  funnel = signal<FunnelShadowDto | null>(null);
  funnelLoaded = signal(false);
  funnelDays = signal(30);

  filings = signal<FilingsIntelligenceDto | null>(null);
  filingsLoaded = signal(false);
  filingsDays = signal(90);
  // Warnings first: for a long-only book the negative deltas are the
  // actionable half (Lazy Prices - language-changers underperform).
  filingsView = signal<'warnings' | 'opportunities'>('warnings');

  secondHop = signal<SecondHopIntelligenceDto | null>(null);
  secondHopLoaded = signal(false);
  secondHopDays = signal(14);

  constructor() {
    this.loadFunnel(30);
    this.loadFilings(90);
    this.loadSecondHop(14);
  }

  loadFunnel(days: number): void {
    this.funnelDays.set(days);
    this.funnelLoaded.set(false);
    this.api.getFunnelShadow(days).subscribe({
      next: (d) => {
        this.funnel.set(d);
        this.funnelLoaded.set(true);
      },
      error: () => this.funnelLoaded.set(true),
    });
  }

  loadFilings(days: number): void {
    this.filingsDays.set(days);
    this.filingsLoaded.set(false);
    this.api.getFilingsIntelligence(days).subscribe({
      next: (d) => {
        this.filings.set(d);
        this.filingsLoaded.set(true);
      },
      error: () => this.filingsLoaded.set(true),
    });
  }

  loadSecondHop(days: number): void {
    this.secondHopDays.set(days);
    this.secondHopLoaded.set(false);
    this.api.getSecondHopIntelligence(days).subscribe({
      next: (d) => {
        this.secondHop.set(d);
        this.secondHopLoaded.set(true);
      },
      error: () => this.secondHopLoaded.set(true),
    });
  }

  // The endpoint returns most-negative-first (the Warnings order); the
  // Opportunities view is the same rows reversed.
  visibleDeltas(): FilingDeltaRowDto[] {
    const rows = this.filings()?.deltas ?? [];
    return this.filingsView() === 'warnings' ? rows : [...rows].reverse();
  }
}
