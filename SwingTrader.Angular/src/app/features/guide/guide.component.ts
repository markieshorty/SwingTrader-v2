import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatIconModule } from '@angular/material/icon';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/services/api.service';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { RiskProfileDto, StrategyWeightsDto, TradingConfigDto } from '../../core/models/dtos';

interface ComponentRow {
  name: string;
  weightKey: keyof StrategyWeightsDto;
  measures: string;
}

// Hybrid manual: static explanatory prose (the concepts) plus this account's
// live configured values (weights, thresholds, risk profile, mode) pulled
// from the same APIs the Settings page edits, so the guide never drifts from
// what the system is actually doing.
@Component({
  selector: 'app-guide',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatTabsModule, MatIconModule, LoadingSpinnerComponent],
  templateUrl: './guide.component.html',
  styleUrl: './guide.component.scss',
})
export class GuideComponent {
  private api = inject(ApiService);

  weights = signal<StrategyWeightsDto | null>(null);
  risk = signal<RiskProfileDto | null>(null);
  config = signal<(TradingConfigDto & { globalRefinementOptIn: boolean }) | null>(null);
  loaded = signal(false);

  // The 8 conviction components in display order, each with a plain-English
  // description of what it measures. The live weight is looked up by key.
  readonly components: ComponentRow[] = [
    { name: 'RSI', weightKey: 'rsiWeight', measures: 'Momentum oscillator — favours oversold-but-recovering names, penalises overbought ones.' },
    { name: 'MACD', weightKey: 'macdWeight', measures: 'Trend/momentum crossover — rewards a rising histogram above the signal line.' },
    { name: 'Volume', weightKey: 'volumeWeight', measures: 'Today’s volume vs its recent average — conviction behind the move.' },
    { name: 'Sentiment', weightKey: 'sentimentWeight', measures: 'Claude’s read of recent company news, scored −1 (bad) to +1 (good).' },
    { name: 'Setup quality', weightKey: 'setupQualityWeight', measures: 'The chart pattern type (oversold recovery, breakout, momentum, etc.).' },
    { name: 'Relative strength', weightKey: 'relativeStrengthWeight', measures: 'The stock’s 5-day return vs its sector ETF — is it leading or lagging its peers.' },
    { name: 'Price level', weightKey: 'priceLevelWeight', measures: 'Where price sits relative to recent support/resistance and breakouts.' },
    { name: 'Fundamental momentum', weightKey: 'fundamentalMomentumWeight', measures: 'Analyst trend, insider activity, earnings consistency and revenue direction.' },
  ];

  // Weights are stored 0–1 and sum to 1.0; show as whole-ish percentages.
  weightPct = (key: keyof StrategyWeightsDto): string => {
    const w = this.weights();
    if (!w) return '—';
    return `${(Number(w[key]) * 100).toFixed(0)}%`;
  };

  pct = (v: number | null | undefined): string => (v == null ? '—' : `${(v * 100).toFixed(0)}%`);

  currentMode = computed(() => this.config()?.tradingMode ?? 'Demo');
  approvalOn = computed(() => this.config()?.approvalRequired ?? false);

  constructor() {
    forkJoin({
      weights: this.api.getStrategyWeights(),
      risk: this.api.getRiskProfile(),
      config: this.api.getAccountSettings(),
    }).subscribe({
      next: ({ weights, risk, config }) => {
        this.weights.set(weights);
        this.risk.set(risk);
        this.config.set(config);
        this.loaded.set(true);
      },
      // A data-source hiccup shouldn't leave the whole manual blank - the
      // static prose is still useful, so render with live values showing "—".
      error: () => this.loaded.set(true),
    });
  }
}
