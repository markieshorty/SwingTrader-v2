import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { environment } from '../../../environments/environment';

interface TrialCard {
  key: string;
  name: string;
  hypothesis: string;
  declaredOn: string;
  status: string;
  evidenceN: number;
  evidenceTarget: number;
  grade: string;
  gatesDecision: string;
  note: string | null;
}
interface Band { label: string; trades: number; avgReturnPct: number; winRatePct: number; }
interface FloorRow { floor: number; skipped: number; skippedAvgPct: number; kept: number; keptAvgPct: number; }
interface TiltSummary {
  scoredTrades: number; tiltedTrades: number;
  equalWeightedAvgPct: number; tiltWeightedAvgPct: number; bands: Band[];
}
interface TrialsDto {
  generatedAt: string;
  closedTrades: number;
  cards: TrialCard[];
  forwardBands: Band[];
  convictionBands: Band[];
  vetoSweep: FloorRow[];
  sizingTilt: TiltSummary;
}

// The trial registry (transparency pivot, 6 Aug 2026): every mechanism that
// claims predictive power, its pre-declared hypothesis, and its evidence so
// far - graded in plain language so nobody (including us) acts on n=20.
@Component({
  selector: 'app-trials',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatIconModule, MatTooltipModule],
  templateUrl: './trials.component.html',
  styleUrl: './trials.component.scss',
})
export class TrialsComponent {
  private readonly http = inject(HttpClient);
  data = signal<TrialsDto | null>(null);
  error = signal(false);

  constructor() {
    this.http.get<TrialsDto>(`${environment.apiUrl}/api/trials`).subscribe({
      next: (d) => this.data.set(d),
      error: () => this.error.set(true),
    });
  }

  progress(c: TrialCard): number {
    return Math.min(100, Math.round((c.evidenceN / c.evidenceTarget) * 100));
  }
}
