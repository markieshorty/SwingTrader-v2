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
  ForwardScorecardDto,
  SecondHopIntelligenceDto,
} from '../../core/models/dtos';

// Read-only evidence page (docs/intelligence-page-plan): filing-language deltas
// and second-hop transmissions. Nothing here mutates anything - it exists so
// the review-before-trust decisions (e.g. raising FD/SH weights) are made with
// the evidence in view.
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

  filings = signal<FilingsIntelligenceDto | null>(null);
  filingsLoaded = signal(false);
  filingsDays = signal(90);
  // Warnings first: for a long-only book the negative deltas are the
  // actionable half (Lazy Prices - language-changers underperform).
  filingsView = signal<'warnings' | 'opportunities'>('warnings');

  secondHop = signal<SecondHopIntelligenceDto | null>(null);
  secondHopLoaded = signal(false);
  secondHopDays = signal(14);

  // The forward-side feedback loop: forward-score buckets, blocked-Buy
  // counterfactuals, shadow-signal correlations. Loaded lazily on first
  // view - it's the heaviest of the three reads (a targeted candle query).
  scorecard = signal<ForwardScorecardDto | null>(null);
  scorecardLoaded = signal(false);
  scorecardDays = signal(90);

  constructor() {
    this.loadFilings(90);
    this.loadSecondHop(14);
  }

  onTabChange(index: number): void {
    if (index === 2 && !this.scorecardLoaded() && this.scorecard() === null) this.loadScorecard(90);
  }

  loadScorecard(days: number): void {
    this.scorecardDays.set(days);
    this.scorecardLoaded.set(false);
    this.api.getForwardScorecard(days).subscribe({
      next: (d) => {
        this.scorecard.set(d);
        this.scorecardLoaded.set(true);
      },
      error: () => this.scorecardLoaded.set(true),
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
