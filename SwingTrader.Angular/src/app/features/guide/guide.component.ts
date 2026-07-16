import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { MatIconModule } from '@angular/material/icon';
import { MatExpansionModule } from '@angular/material/expansion';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/services/api.service';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { RiskProfileDto, StrategyWeightsDto, TradingConfigDto } from '../../core/models/dtos';
import { HOLD_CEILING_MULTIPLE } from '../../core/constants';

interface ComponentRow {
  name: string;
  weightKey: keyof StrategyWeightsDto;
  // One-line summary shown in the accordion header.
  measures: string;
  // Plain-English explanation (assumes no trading background) shown when the
  // row is expanded.
  plain: string;
}

// Hybrid manual: static explanatory prose (the concepts) plus this account's
// live configured values (weights, thresholds, risk profile, mode) pulled
// from the same APIs the Settings page edits, so the guide never drifts from
// what the system is actually doing.
@Component({
  selector: 'app-guide',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatTabsModule, MatIconModule, MatExpansionModule, LoadingSpinnerComponent],
  templateUrl: './guide.component.html',
  styleUrl: './guide.component.scss',
})
export class GuideComponent {
  private api = inject(ApiService);

  weights = signal<StrategyWeightsDto | null>(null);
  risk = signal<RiskProfileDto | null>(null);
  config = signal<(TradingConfigDto & { globalRefinementOptIn: boolean }) | null>(null);
  loaded = signal(false);

  // The absolute time-cap backstop: a runner is force-closed once it reaches
  // HOLD_CEILING_MULTIPLE x its guide hold, rounded up. Mirrors the backend
  // PositionMonitorService time exit.
  hardHoldCeiling(guideHoldDays: number | null | undefined): number | null {
    if (guideHoldDays == null) return null;
    return Math.ceil(guideHoldDays * HOLD_CEILING_MULTIPLE);
  }

  // The 6 GATE components in display order, each with a plain-English
  // description of what it measures. These are the checks that feed the 0–10
  // conviction (gate) score which decides Buy / Watch / Hold / Avoid. Their
  // weights sum to 100%. The live weight is looked up by key. The two FORWARD
  // components (sentiment + fundamental) live in forwardComponents below — they
  // don't affect the gate score, they drive position sizing and the veto.
  readonly gateComponents: ComponentRow[] = [
    {
      name: 'RSI',
      weightKey: 'rsiWeight',
      measures: 'Is the stock oversold and bouncing, or overbought and stretched?',
      plain:
        'RSI (Relative Strength Index) is a momentum gauge that runs from 0 to 100, measuring how ' +
        'hard a stock has been bought or sold recently. A low reading (below ~35) means it has been ' +
        'heavily sold off — often "oversold" and due a bounce. A high reading (above ~70) means it ' +
        'has been bought aggressively — "overbought" and prone to a pullback. This component scores ' +
        'highest for stocks lifting off an oversold low and lowest for ones that look overbought.',
    },
    {
      name: 'MACD',
      weightKey: 'macdWeight',
      measures: 'Is upward momentum building in the price trend?',
      plain:
        'MACD (Moving Average Convergence Divergence) compares a short-term and a longer-term average ' +
        'of the price to read the trend. When the short-term average pulls above the long-term one and ' +
        'the gap is widening, momentum is turning up — the bullish case. This scores well when momentum ' +
        'is positive and accelerating, and poorly when it is fading or turning down.',
    },
    {
      name: 'Volume',
      weightKey: 'volumeWeight',
      measures: 'Is there real buying conviction behind the move?',
      plain:
        'Volume is how many shares changed hands, compared here to the stock’s typical recent day. A ' +
        'price move on heavy volume means a lot of people acted on it, so it is more trustworthy. The ' +
        'same move on light volume is easier to reverse. Higher-than-usual volume scores better.',
    },
    {
      name: 'Setup quality',
      weightKey: 'setupQualityWeight',
      measures: 'How reliable is the chart pattern that triggered the signal?',
      plain:
        'A "setup" is the chart pattern behind the signal — for example bouncing up off a recent low ' +
        '(oversold recovery), pushing above a prior ceiling (breakout), or riding an existing uptrend ' +
        '(momentum continuation). Some patterns have historically led to better trades than others, ' +
        'and this component scores each accordingly.',
    },
    {
      name: 'Relative strength',
      weightKey: 'relativeStrengthWeight',
      measures: 'Is the stock outperforming or lagging its sector?',
      plain:
        'This compares the stock’s last-5-day return against its sector ETF (a basket of similar ' +
        'companies). A stock beating its peers is a "leader", and leaders tend to keep leading; one ' +
        'lagging its peers is a "laggard". Outperformance scores higher.',
    },
    {
      name: 'Price level',
      weightKey: 'priceLevelWeight',
      measures: 'Where is the price versus recent support and resistance?',
      plain:
        '"Support" is a price floor buyers have repeatedly defended; "resistance" is a ceiling sellers ' +
        'have repeatedly defended. Bouncing up off support, or breaking cleanly above resistance, is ' +
        'constructive and scores well. Stalling just under resistance scores poorly.',
    },
  ];

  // The 2 FORWARD components. These do NOT feed the 0–10 gate/conviction score
  // that decides Buy / Watch / Hold / Avoid. Instead they blend into a separate
  // Forward score (their weights sum to 100% between themselves) that tilts
  // position size and can veto a Buy — see the "Risk & your money" tab.
  readonly forwardComponents: ComponentRow[] = [
    {
      name: 'Sentiment',
      weightKey: 'forwardSentimentWeight',
      measures: 'What is the tone of recent news about the company?',
      plain:
        'Claude reads the company’s recent news headlines and rates the overall tone from very negative ' +
        'to very positive. Good news (upgrades, strong results, deals) lifts the score; bad news ' +
        '(downgrades, misses, scandals) lowers it.',
    },
    {
      name: 'Fundamental momentum',
      weightKey: 'forwardFundamentalWeight',
      measures: 'Is the underlying business improving?',
      plain:
        'Unlike the gate components (which read the chart), this looks at the business itself: whether ' +
        'analysts are turning more positive, whether company insiders are buying their own stock, ' +
        'whether the company has a habit of beating earnings, and whether revenue forecasts are rising. ' +
        'A strengthening business scores higher.',
    },
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

  // The market backdrop the system currently reads (Bull/Neutral/Bear/Crisis).
  liveRegime = computed(() => this.risk()?.currentRegime ?? '—');
  // Whether the Default master book is switched on. When it is, that single
  // book governs live trading regardless of the detected regime; when off, the
  // book matching the live regime governs.
  defaultBookOn = computed(() => this.risk()?.defaultRegimeEnabled ?? false);
  // The name of the book actually governing live decisions right now.
  governingBook = computed(() => (this.defaultBookOn() ? 'Default' : this.liveRegime()));
  // Whether the governing book pauses new entries.
  autopauseOn = computed(() => this.risk()?.autopauseTrading ?? false);

  // Worked £100 example of where the default stop loss would sit.
  stopExample = computed(() => {
    const pct = this.weights()?.stopLossPctDefault;
    return pct == null ? '—' : (100 * (1 - pct)).toFixed(2);
  });

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
