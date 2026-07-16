import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatDialog } from '@angular/material/dialog';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { HOLD_CEILING_MULTIPLE } from '../../core/constants';
import { ConfirmDeleteDialogComponent } from '../../shared/components/confirm-delete-dialog/confirm-delete-dialog.component';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  AccountMemberDto,
  ApiKeyProvider,
  ConnectionState,
  KeyStatusesDto,
  KeyTestResult,
  MarketRegimeName,
  NotificationRecipientDto,
  RiskProfileDto,
  SetupTacticsDto,
  SetupTacticsRowDto,
  SetupTypeName,
  UpdateSetupTacticsDto,
  StrategyWeightsDto,
  TradingMode,
  UpdateRiskProfileDto,
} from '../../core/models/dtos';
import { readTabIndexFromRoute, writeTabIndexToRoute } from '../../shared/utils/tab-route.util';
import { errorMessage } from '../../shared/utils/error-message.util';

const TAB_NAMES = ['api-keys', 'trading', 'strategy', 'risk', 'setups', 'notifications', 'account'] as const;

const SETUP_LABELS: Record<SetupTypeName, string> = {
  OversoldRecovery: 'Oversold recovery',
  Breakout: 'Breakout',
  MomentumContinuation: 'Momentum continuation',
  VolumeSpike: 'Volume spike',
  TrendFollowing: 'Trend following',
};

// The six GATE weights (sum to 1) — decide Buy/Watch/Hold/Avoid.
const COMPONENT_WEIGHT_FIELDS: { key: keyof StrategyWeightsDto; label: string }[] = [
  { key: 'rsiWeight', label: 'RSI' },
  { key: 'macdWeight', label: 'MACD' },
  { key: 'volumeWeight', label: 'Volume' },
  { key: 'setupQualityWeight', label: 'Setup Quality' },
  { key: 'relativeStrengthWeight', label: 'Relative Strength' },
  { key: 'priceLevelWeight', label: 'Price Level' },
];

// The forward-score blend (sum to 1) — drives sizing/veto, not entry selection.
const FORWARD_WEIGHT_FIELDS: { key: keyof StrategyWeightsDto; label: string }[] = [
  { key: 'forwardSentimentWeight', label: 'Sentiment' },
  { key: 'forwardFundamentalWeight', label: 'Fundamental Momentum' },
];

const PROVIDER_LABELS: Partial<Record<ApiKeyProvider, string>> = {
  Finnhub: 'Finnhub',
  Tiingo: 'Tiingo',
  Trading212DemoKey: 'Trading 212 API Key (Demo)',
  Trading212DemoSecret: 'Trading 212 API Secret (Demo)',
  Trading212LiveKey: 'Trading 212 API Key (Live)',
  Trading212LiveSecret: 'Trading 212 API Secret (Live)',
};

function toUpdateRiskProfileDto(profile: RiskProfileDto): UpdateRiskProfileDto {
  return {
    lockedCapitalPct: profile.lockedCapitalPct,
    maxOpenPositions: profile.maxOpenPositions,
    dailyLossCircuitBreakerPct: profile.dailyLossCircuitBreakerPct,
    maxHoldDays: profile.maxHoldDays,
    trailingActivationPct: profile.trailingActivationPct,
    trailingDistancePct: profile.trailingDistancePct,
    earningsGateDays: profile.earningsGateDays,
    minHoldDays: profile.minHoldDays,
    momentumHealthThreshold: profile.momentumHealthThreshold,
    regime: profile.regime,
    autopauseTrading: profile.autopauseTrading,
    stopLossPct: profile.stopLossPct,
    targetPct: profile.targetPct,
    sizingMode: profile.sizingMode,
    flatPositionPct: profile.flatPositionPct,
    sizingAggressiveness: profile.sizingAggressiveness,
    forwardVetoFloor: profile.forwardVetoFloor,
  };
}

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatTabsModule,
    MatSlideToggleModule,
    MatSelectModule,
    MatSliderModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  private dialog = inject(MatDialog);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private titleService = inject(Title);
  auth = inject(AuthService);

  providers = Object.keys(PROVIDER_LABELS) as ApiKeyProvider[];
  providerLabel = (p: ApiKeyProvider) => PROVIDER_LABELS[p];

  selectedTabIndex = signal(0);

  onTabChange(index: number): void {
    this.selectedTabIndex.set(index);
    writeTabIndexToRoute(this.router, this.route, TAB_NAMES, index, this.titleService, 'Settings');
  }

  keyStatuses = signal<KeyStatusesDto | null>(null);
  editingProvider = signal<ApiKeyProvider | null>(null);
  keyInput = '';
  // Per-pair connection indicator. Null = derive from saved key status
  // (so returning to the page shows the last known state); a set value is
  // the result of a connect click this session.
  private connectStates = signal<Record<'Demo' | 'Live', ConnectionState | null>>({ Demo: null, Live: null });

  private readonly trading212Providers: ApiKeyProvider[] = [
    'Trading212DemoKey', 'Trading212DemoSecret', 'Trading212LiveKey', 'Trading212LiveSecret',
  ];

  isTrading212 = (p: ApiKeyProvider) => this.trading212Providers.includes(p);

  private pairProviders(mode: 'Demo' | 'Live'): [ApiKeyProvider, ApiKeyProvider] {
    return mode === 'Demo'
      ? ['Trading212DemoKey', 'Trading212DemoSecret']
      : ['Trading212LiveKey', 'Trading212LiveSecret'];
  }

  // A pair can be connected only once both its key and secret are saved.
  canConnect(mode: 'Demo' | 'Live'): boolean {
    const s = this.keyStatuses();
    if (!s) return false;
    const [key, secret] = this.pairProviders(mode);
    return s[key] !== 'NotSet' && s[secret] !== 'NotSet';
  }

  // The indicator to render next to a pair's connect button. A live click
  // result wins; otherwise fall back to the saved key status so the icon
  // reflects the last known connection.
  pairState(mode: 'Demo' | 'Live'): ConnectionState {
    const override = this.connectStates()[mode];
    if (override) return override;

    const s = this.keyStatuses();
    const [key, secret] = this.pairProviders(mode);
    if (!s || s[key] === 'NotSet' || s[secret] === 'NotSet') return { status: 'idle', text: 'Add key + secret, then connect' };
    if (s[key] === 'Valid' && s[secret] === 'Valid') return { status: 'success', text: 'Connected' };
    if (s[key] === 'Invalid' || s[secret] === 'Invalid') return { status: 'error', text: 'Last connection failed' };
    return { status: 'idle', text: 'Not yet connected' };
  }

  connectTrading212(mode: 'Demo' | 'Live'): void {
    this.connectStates.update((m) => ({ ...m, [mode]: { status: 'connecting', text: 'Connecting…' } }));
    this.api.testTrading212Pair(mode).subscribe({
      next: (result) => {
        this.connectStates.update((m) => ({
          ...m,
          [mode]: { status: result.valid ? 'success' : 'error', text: this.keyTestMessage(result) },
        }));
        this.loadKeyStatuses();
      },
      error: (err) => {
        this.connectStates.update((m) => ({
          ...m,
          [mode]: { status: 'error', text: errorMessage(err, `Could not connect to ${mode.toLowerCase()}.`) },
        }));
      },
    });
  }

  // The confirmed AppUser record, not the raw auth token - the token's
  // email claim is frequently a synthetic {objectId}@tenant fallback for
  // identity providers that don't return a real one.
  me = signal<{ email: string; displayName: string } | null>(null);
  editingEmail = signal(false);
  emailInput = '';

  hasDemoPair = computed(() => {
    const s = this.keyStatuses();
    return !!s && s.Trading212DemoKey !== 'NotSet' && s.Trading212DemoSecret !== 'NotSet';
  });
  hasLivePair = computed(() => {
    const s = this.keyStatuses();
    return !!s && s.Trading212LiveKey !== 'NotSet' && s.Trading212LiveSecret !== 'NotSet';
  });

  tradingMode = signal<TradingMode>('Demo');
  // The mode currently persisted server-side (vs. tradingMode(), which tracks
  // the unsaved UI selection) - used to detect an actual Demo/Live switch on
  // save so we can hard-reload for the new mode's money/positions data.
  private persistedTradingMode: TradingMode = 'Demo';
  // Public accessor for the template - the pause switch is scoped to the mode
  // actually in effect server-side, not the unsaved dropdown selection.
  get currentTradingMode(): TradingMode {
    return this.persistedTradingMode;
  }
  approvalRequired = signal(true);
  // Pause switch for new-position executions ("entries"), scoped to the
  // current mode (persistedTradingMode). Applied immediately on toggle, not
  // via Save. Reason distinguishes a manual pause from a circuit-breaker
  // auto-pause so the copy can explain it.
  executionPaused = signal(false);
  executionPauseReason = signal<'Manual' | 'CircuitBreaker'>('Manual');
  t212AccountId = signal<string | null>(null);
  globalRefinementOptIn = signal(false);
  // Only the Owner can delete the account (enforced server-side too) - hide
  // the button from Members entirely rather than let them click it and hit
  // a 403, since Danger Zone actions shouldn't invite trial-and-error.
  isOwner = signal(false);

  recipients = signal<NotificationRecipientDto[]>([]);
  newRecipientEmail = '';

  members = signal<AccountMemberDto[]>([]);
  inviteEmail = '';
  inviteLink = signal<string | null>(null);

  weights = signal<StrategyWeightsDto | null>(null);
  componentWeightFields = COMPONENT_WEIGHT_FIELDS;
  forwardWeightFields = FORWARD_WEIGHT_FIELDS;

  weightsSum = computed(() => {
    const w = this.weights();
    if (!w) return 0;
    return COMPONENT_WEIGHT_FIELDS.reduce((sum, f) => sum + (Number(w[f.key]) || 0), 0);
  });

  weightsSumValid = computed(() => Math.abs(this.weightsSum() - 1) < 0.001);

  forwardWeightsSum = computed(() => {
    const w = this.weights();
    if (!w) return 0;
    return FORWARD_WEIGHT_FIELDS.reduce((sum, f) => sum + (Number(w[f.key]) || 0), 0);
  });

  forwardWeightsSumValid = computed(() => Math.abs(this.forwardWeightsSum() - 1) < 0.001);

  // Per-setup entry/exit tactics (docs/setup-tactics-plan). Editable working
  // copies per row; saved per row via saveSetupRow().
  setupTactics = signal<SetupTacticsDto | null>(null);
  setupTacticsDraft = signal<SetupTacticsRowDto[]>([]);
  setupLabel = (s: SetupTypeName): string => SETUP_LABELS[s];

  private loadSetupTactics(): void {
    this.api.getSetupTactics().subscribe({
      next: (t) => {
        this.setupTactics.set(t);
        this.setupTacticsDraft.set(t.setups.map((r) => ({ ...r })));
      },
      error: () => {
        this.setupTactics.set(null);
        this.setupTacticsDraft.set([]);
      },
    });
  }

  updateSetupField(setup: SetupTypeName, key: keyof SetupTacticsRowDto, value: number): void {
    this.setupTacticsDraft.update((rows) =>
      rows.map((r) => (r.setupType === setup ? { ...r, [key]: value } : r)));
  }

  saveSetupRow(setup: SetupTypeName): void {
    const row = this.setupTacticsDraft().find((r) => r.setupType === setup);
    if (!row) return;
    const payload: UpdateSetupTacticsDto = { ...row };
    this.api.updateSetupTactics(payload).subscribe({
      next: () => this.snackbar.open(`${SETUP_LABELS[setup]} tactics saved`, 'Dismiss', { duration: 3000 }),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to save.'), 'Dismiss', { duration: 4000 }),
    });
  }

  // The live on/off switch applies immediately (unlike the tactic numbers,
  // which batch behind a Save button) - the whole row is sent so the current
  // stop/target/etc. ride along unchanged. On failure the toggle reverts.
  toggleSetupEnabled(setup: SetupTypeName, enabled: boolean): void {
    const prev = this.setupTacticsDraft().find((r) => r.setupType === setup)?.enabled ?? true;
    this.setupTacticsDraft.update((rows) =>
      rows.map((r) => (r.setupType === setup ? { ...r, enabled } : r)));
    const row = this.setupTacticsDraft().find((r) => r.setupType === setup);
    if (!row) return;
    this.api.updateSetupTactics({ ...row }).subscribe({
      next: () => this.snackbar.open(
        `${SETUP_LABELS[setup]} ${enabled ? 'enabled — can trade live' : 'disabled — signals show as Watch, never Buy'}`,
        'Dismiss', { duration: 3500 }),
      error: (err) => {
        this.setupTacticsDraft.update((rows) =>
          rows.map((r) => (r.setupType === setup ? { ...r, enabled: prev } : r)));
        this.snackbar.open(errorMessage(err, 'Failed to update.'), 'Dismiss', { duration: 4000 });
      },
    });
  }

  riskProfile = signal<RiskProfileDto | null>(null);
  // Working copy the sliders bind to - saved explicitly via saveRiskProfile(),
  // so a user can cancel/reload without their in-progress drag committing.
  riskProfileDraft = signal<UpdateRiskProfileDto | null>(null);

  // The regime book currently being edited, and the live regime the account is
  // actually in (drives the "active now" badge on the selector).
  selectedRegime = signal<MarketRegimeName>('Neutral');
  readonly regimeOptions: MarketRegimeName[] = ['Bull', 'Neutral', 'Bear', 'Crisis'];
  currentRegime = computed(() => this.riskProfile()?.currentRegime ?? null);

  riskProfileDirty = computed(() => {
    const original = this.riskProfile();
    const draft = this.riskProfileDraft();
    if (!original || !draft) return false;
    return (
      original.lockedCapitalPct !== draft.lockedCapitalPct ||
      original.maxOpenPositions !== draft.maxOpenPositions ||
      original.dailyLossCircuitBreakerPct !== draft.dailyLossCircuitBreakerPct ||
      original.sizingMode !== draft.sizingMode ||
      original.flatPositionPct !== draft.flatPositionPct ||
      original.sizingAggressiveness !== draft.sizingAggressiveness ||
      original.forwardVetoFloor !== draft.forwardVetoFloor ||
      original.maxHoldDays !== draft.maxHoldDays ||
      original.trailingActivationPct !== draft.trailingActivationPct ||
      original.trailingDistancePct !== draft.trailingDistancePct ||
      original.earningsGateDays !== draft.earningsGateDays ||
      original.minHoldDays !== draft.minHoldDays ||
      original.momentumHealthThreshold !== draft.momentumHealthThreshold ||
      original.autopauseTrading !== draft.autopauseTrading
    );
  });

  // MinHoldDays and MaxHoldDays constrain each other live as the user drags
  // either slider — auto-adjusting the other rather than blocking the drag,
  // since the user is trying to reach a valid configuration either way.
  shortConfirmedPhaseWarning = computed(() => {
    const draft = this.riskProfileDraft();
    if (!draft) return false;
    return draft.maxHoldDays - draft.minHoldDays < 2;
  });

  // The absolute time-cap backstop: a runner is force-closed once it reaches
  // HOLD_CEILING_MULTIPLE x its guide hold, rounded up. Mirrors the backend
  // PositionMonitorService time exit.
  hardHoldCeiling(guideHoldDays: number | null | undefined): number | null {
    if (guideHoldDays == null) return null;
    return Math.ceil(guideHoldDays * HOLD_CEILING_MULTIPLE);
  }

  riskLivePreview = computed(() => {
    const draft = this.riskProfileDraft();
    const breakdown = this.riskProfile()?.capitalBreakdown;
    if (!draft) return null;
    // Deployable (active) capital = the un-locked share (total − locked); each
    // position is FlatPositionPct of the whole portfolio.
    const total = breakdown?.totalCapital ?? null;
    const locked = total !== null ? total * draft.lockedCapitalPct : null;
    const active = total !== null && locked !== null ? total - locked : null;
    const activePct = 1 - draft.lockedCapitalPct;
    const maxPerTrade = total !== null ? total * draft.flatPositionPct : null;
    return { total, locked, active, activePct, maxPerTrade };
  });

  // Per-trade sizing preview. In Flat mode (or Funnel with aggressiveness 0)
  // every position is the flat base. In Funnel mode with aggressiveness > 0 the
  // Forward score tilts each position within a band of base × (1 ± MaxTilt ×
  // aggressiveness) — mirrors PositionSizingService.ComputeForwardMultiplier
  // (CapitalRules.MaxSizingTilt = 0.5).
  sizingPreview = computed(() => {
    const draft = this.riskProfileDraft();
    const total = this.riskLivePreview()?.total ?? null;
    if (!draft || total === null) return null;
    const base = total * draft.flatPositionPct;
    const MAX_TILT = 0.5;
    const tiltPct = draft.sizingMode === 'Funnel' ? MAX_TILT * draft.sizingAggressiveness : 0;
    return {
      total,
      base,
      tilted: tiltPct > 0,
      tiltPct,                       // fraction, e.g. 0.25 = ±25%
      low: base * (1 - tiltPct),
      high: base * (1 + tiltPct),
      maxDeployed: base * draft.maxOpenPositions,
    };
  });

  constructor() {
    this.loadKeyStatuses();
    this.loadAccountSettings();
    this.loadRecipients();
    this.loadMembers();
    this.loadWeights();
    this.loadRiskProfile();
    this.loadSetupTactics();
    this.api.getMe().subscribe({ next: (me) => this.me.set({ email: me.email, displayName: me.displayName }) });
    this.selectedTabIndex.set(readTabIndexFromRoute(this.route, TAB_NAMES));
  }

  private loadRiskProfile(regime?: MarketRegimeName): void {
    this.api.getRiskProfile(regime).subscribe({
      next: (profile) => {
        this.riskProfile.set(profile);
        this.riskProfileDraft.set(toUpdateRiskProfileDto(profile));
        this.selectedRegime.set(profile.regime);
      },
      error: () => {
        this.riskProfile.set(null);
        this.riskProfileDraft.set(null);
      },
    });
  }

  // Switch which regime book is being edited. Unsaved edits to the current book
  // are discarded (the sliders reload from the newly selected book).
  selectRegime(regime: MarketRegimeName): void {
    if (regime === this.selectedRegime()) return;
    if (this.riskProfileDirty()) {
      this.dialog
        .open(ConfirmDialogComponent, {
          data: {
            title: 'Discard unsaved changes?',
            message: `You have unsaved changes to the ${this.selectedRegime()} book. Switch to ${regime} and discard them?`,
            cancelLabel: 'Stay',
            confirmLabel: 'Discard & switch',
            confirmColor: 'warn',
          },
          width: '420px',
        })
        .afterClosed()
        .subscribe((confirmed) => {
          if (confirmed) this.loadRiskProfile(regime);
        });
      return;
    }
    this.loadRiskProfile(regime);
  }

  updateRiskDraftField(key: keyof UpdateRiskProfileDto, value: number | boolean | string): void {
    const draft = this.riskProfileDraft();
    if (!draft) return;

    if (key === 'minHoldDays' && typeof value === 'number' && value >= draft.maxHoldDays) {
      // Auto-adjust rather than reject — the user is dragging toward a valid
      // configuration either way, so nudge the other bound instead of blocking.
      this.riskProfileDraft.set({ ...draft, minHoldDays: value, maxHoldDays: value + 1 });
      this.snackbar.open(`Maximum hold period adjusted to ${value + 1} days to stay above probation period.`, 'Dismiss', { duration: 4000 });
      return;
    }

    if (key === 'maxHoldDays' && typeof value === 'number' && value <= draft.minHoldDays) {
      this.riskProfileDraft.set({ ...draft, maxHoldDays: value, minHoldDays: Math.max(1, value - 1) });
      this.snackbar.open(`Probation period adjusted to ${Math.max(1, value - 1)} days to stay below maximum hold period.`, 'Dismiss', { duration: 4000 });
      return;
    }

    this.riskProfileDraft.set({ ...draft, [key]: value });
  }

  saveRiskProfile(): void {
    const draft = this.riskProfileDraft();
    if (!draft) return;

    this.api.updateRiskProfile(draft).subscribe({
      next: () => {
        this.snackbar.open('Risk profile saved', 'Dismiss', { duration: 3000 });
        this.loadRiskProfile();
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to save.'), 'Dismiss', { duration: 4000 }),
    });
  }

  resetRiskProfile(): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Reset risk book',
          message: `Reset the ${this.selectedRegime()} risk book to its default posture? Your current settings for this regime will be lost.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Reset',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.resetRiskProfile(this.selectedRegime()).subscribe({
          next: () => {
            this.snackbar.open('Risk profile reset to defaults', 'Dismiss', { duration: 3000 });
            this.loadRiskProfile();
          },
          error: (err) => this.snackbar.open(errorMessage(err, 'Failed to reset.'), 'Dismiss', { duration: 4000 }),
        });
      });
  }

  discardRiskProfileChanges(): void {
    const original = this.riskProfile();
    if (original) this.riskProfileDraft.set(toUpdateRiskProfileDto(original));
  }

  private loadWeights(): void {
    this.api.getStrategyWeights().subscribe({
      next: (weights) => this.weights.set(weights),
      error: () => this.weights.set(null),
    });
  }

  updateWeightField(key: keyof StrategyWeightsDto, value: string): void {
    const current = this.weights();
    if (!current) return;
    this.weights.set({ ...current, [key]: Number(value) });
  }

  saveWeights(): void {
    const weights = this.weights();
    if (!weights) return;

    if (!this.weightsSumValid()) {
      this.snackbar.open(`Gate weights must sum to 1.0 — currently ${this.weightsSum().toFixed(3)}.`, 'Dismiss', { duration: 5000 });
      return;
    }
    if (!this.forwardWeightsSumValid()) {
      this.snackbar.open(`Forward weights must sum to 1.0 — currently ${this.forwardWeightsSum().toFixed(3)}.`, 'Dismiss', { duration: 5000 });
      return;
    }

    this.api.updateStrategyWeights(weights).subscribe({
      next: () => this.snackbar.open('Strategy weights saved', 'Dismiss', { duration: 3000 }),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to save.'), 'Dismiss', { duration: 4000 }),
    });
  }

  private loadKeyStatuses(): void {
    this.api.getKeyStatuses().subscribe({
      next: (statuses) => this.keyStatuses.set(statuses),
      error: () => this.keyStatuses.set(null),
    });
  }

  private loadAccountSettings(): void {
    this.api.getAccountSettings().subscribe({
      next: (settings) => {
        this.tradingMode.set(settings.tradingMode);
        this.persistedTradingMode = settings.tradingMode;
        this.approvalRequired.set(settings.approvalRequired);
        this.t212AccountId.set(settings.t212AccountId);
        this.globalRefinementOptIn.set(settings.globalRefinementOptIn);
        this.executionPaused.set(settings.executionPaused);
        this.executionPauseReason.set(settings.executionPauseReason);
        this.isOwner.set(settings.role === 'Owner');
      },
    });
  }

  private loadRecipients(): void {
    this.api.getNotificationRecipients().subscribe({
      next: (recipients) => this.recipients.set(recipients),
      error: () => this.recipients.set([]),
    });
  }

  private loadMembers(): void {
    this.api.getMembers().subscribe({
      next: (members) => this.members.set(members),
      error: () => this.members.set([]),
    });
  }

  statusLabel(provider: ApiKeyProvider): string {
    const status = this.keyStatuses()?.[provider] ?? 'NotSet';
    return { NotSet: 'Not configured', SetNotTested: 'Saved — not tested', Valid: '✓ Connected', Invalid: '✗ Connection failed' }[status];
  }

  statusClass(provider: ApiKeyProvider): string {
    return (this.keyStatuses()?.[provider] ?? 'NotSet').toLowerCase();
  }

  startEditing(provider: ApiKeyProvider): void {
    this.editingProvider.set(provider);
    this.keyInput = '';
  }

  cancelEditing(): void {
    this.editingProvider.set(null);
    this.keyInput = '';
  }

  saveKey(): void {
    const provider = this.editingProvider();
    if (!provider || !this.keyInput.trim()) return;

    // Trading212 keys are verified per-pair via the Connect buttons, so a
    // T212 save skips the connectivity test (test=false) - both to avoid a
    // lone-key test that can't authenticate, and to keep off T212's rate
    // limit if the user immediately hits Connect. Finnhub/Tiingo still test
    // on save so their status chip updates right away.
    const shouldTest = !this.isTrading212(provider);
    this.api.saveKey(provider, this.keyInput.trim(), shouldTest).subscribe({
      next: (result) => {
        if (shouldTest) this.snackbar.open(this.keyTestMessage(result), 'Dismiss', { duration: 8000 });
        else this.snackbar.open('Saved — use Connect to verify.', 'Dismiss', { duration: 4000 });
        this.cancelEditing();
        this.loadKeyStatuses();
      },
      error: () => this.snackbar.open('Failed to save key', 'Dismiss', { duration: 4000 }),
    });
  }

  testKey(provider: ApiKeyProvider): void {
    this.api.testKey(provider).subscribe({
      next: (result) => {
        this.snackbar.open(this.keyTestMessage(result), 'Dismiss', { duration: 8000 });
        this.loadKeyStatuses();
      },
    });
  }

  // For a verified Trading212 pair, echo the account balance + environment so
  // the user can confirm the key/secret are correct and not swapped - e.g.
  // "Connected to Live account (real money) — £5,000.00 total, £1,234.50 free".
  private keyTestMessage(result: KeyTestResult): string {
    if (!result.valid) return result.message || 'Key test failed';
    if (result.cashTotal === null) return result.message || 'Key is valid';

    const ccy = result.currency ?? '';
    const total = `${ccy}${result.cashTotal.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    const free = result.cashFree !== null
      ? `, ${ccy}${result.cashFree.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} free`
      : '';
    return `${result.message} — ${total} total${free}`;
  }

  removeKey(provider: ApiKeyProvider): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Remove API key',
          message: `Remove the ${this.providerLabel(provider)} key? You will need to re-enter it to use this integration again.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Remove',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.deleteKey(provider).subscribe({
          next: () => this.loadKeyStatuses(),
          error: (err) => this.snackbar.open(errorMessage(err, 'Failed to remove key.'), 'Dismiss', { duration: 4000 }),
        });
      });
  }

  saveTradingConfig(force = false): void {
    const enablingApprovals = this.approvalRequired();
    const noApprovalRecipient = !this.recipients().some(r => r.tradeApprovalEnabled);
    const modeChanged = this.tradingMode() !== this.persistedTradingMode;
    this.api.updateTradingConfig(this.tradingMode(), this.approvalRequired(), force).subscribe({
      next: () => {
        // A mode switch changes which account (Demo vs Live) every
        // money/positions view reads from. Hard-reload so the dashboard,
        // positions, portfolio cards and activity feed all re-fetch for the
        // new mode rather than showing stale figures from the old one until
        // the next 60s poll. Only on an actual switch - a plain
        // approval-toggle save keeps the SPA in place.
        if (modeChanged) {
          this.snackbar.open(`Switched to ${this.tradingMode()} — reloading…`, undefined, { duration: 1500 });
          setTimeout(() => window.location.reload(), 600);
          return;
        }
        if (enablingApprovals && noApprovalRecipient) {
          const ref = this.snackbar.open(
            'Approval required is on, but no recipient has "Receive trade approval emails" enabled.',
            'Set up now',
            { duration: 8000 },
          );
          ref.onAction().subscribe(() => {
            const idx = TAB_NAMES.indexOf('notifications');
            this.selectedTabIndex.set(idx);
            writeTabIndexToRoute(this.router, this.route, TAB_NAMES, idx, this.titleService, 'Settings');
          });
        } else {
          this.snackbar.open('Trading settings saved', 'Dismiss', { duration: 3000 });
        }
      },
      error: (err) => {
        // The open-positions block returns canForce=true for admins, who can
        // knowingly switch mode anyway (leaving those positions unmonitored).
        // Offer the override rather than dead-ending them.
        if (err?.error?.canForce) {
          this.dialog
            .open(ConfirmDialogComponent, {
              data: {
                title: 'Open positions in the current mode',
                message: `${errorMessage(err, 'You have open positions.')}\n\nSwitching anyway leaves them unmonitored — no stop-loss, target, trailing-stop or time-exit enforcement until you switch back.`,
                cancelLabel: 'OK, no change',
                confirmLabel: "I don't care, change mode",
                confirmColor: 'warn',
              },
              width: '460px',
            })
            .afterClosed()
            .subscribe((confirmed) => {
              if (confirmed) this.saveTradingConfig(true);
            });
          return;
        }
        this.snackbar.open(errorMessage(err, 'Failed to save.'), 'Dismiss', { duration: 4000 });
      },
    });
  }

  toggleGlobalRefinement(enabled: boolean): void {
    this.globalRefinementOptIn.set(enabled);
    this.api.setGlobalRefinementOptIn(enabled).subscribe();
  }

  toggleExecutionPaused(paused: boolean): void {
    this.executionPaused.set(paused);
    // A manual toggle always sets the reason to Manual server-side; reflect
    // that locally so a circuit-breaker note clears the moment they resume.
    this.executionPauseReason.set('Manual');

    this.api.setExecutionPaused(paused).subscribe({
      next: () =>
        this.snackbar.open(
          paused
            ? `${this.persistedTradingMode} entries paused — exits still run`
            : `${this.persistedTradingMode} entries resumed`,
          'Dismiss',
          { duration: 3000 },
        ),
      error: () => {
        // Revert the optimistic flip if the server rejected it.
        this.executionPaused.set(!paused);
        this.snackbar.open('Could not update pause state', 'Dismiss', { duration: 3000 });
      },
    });
  }

  startEditingEmail(): void {
    this.emailInput = this.me()?.email ?? '';
    this.editingEmail.set(true);
  }

  saveEmail(): void {
    const email = this.emailInput.trim();
    if (!email || !email.includes('@')) return;

    this.api.updateMyEmail(email).subscribe({
      next: () => {
        this.editingEmail.set(false);
        this.api.getMe().subscribe({ next: (me) => this.me.set({ email: me.email, displayName: me.displayName }) });
        this.snackbar.open('Email address updated', 'Dismiss', { duration: 3000 });
      },
      error: () => this.snackbar.open('Failed to update email address.', 'Dismiss', { duration: 4000 }),
    });
  }

  addRecipient(): void {
    if (!this.newRecipientEmail.trim()) return;
    // Every category except TradeApproval - that one is off by default and
    // opted into per-recipient via the toggle below.
    this.api.addNotificationRecipient(this.newRecipientEmail.trim(), 31).subscribe({
      next: () => {
        this.newRecipientEmail = '';
        this.loadRecipients();
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to add recipient.'), 'Dismiss', { duration: 4000 }),
    });
  }

  removeRecipient(id: number, email: string): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Remove recipient',
          message: `Remove ${email} from notification recipients?`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Remove',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.removeNotificationRecipient(id).subscribe({ next: () => this.loadRecipients() });
      });
  }

  toggleTradeApproval(recipient: NotificationRecipientDto, enabled: boolean): void {
    this.api.setTradeApproval(recipient.id, enabled).subscribe({
      next: () => {
        this.loadRecipients();
        this.snackbar.open(enabled ? 'Trade approval emails enabled.' : 'Trade approval emails disabled.', 'Dismiss', { duration: 3000 });
      },
      error: () => this.snackbar.open('Failed to update trade approval setting.', 'Dismiss', { duration: 4000 }),
    });
  }

  createInvite(): void {
    if (!this.inviteEmail.trim()) return;

    this.api.createInvite(this.inviteEmail.trim(), window.location.origin).subscribe({
      next: (result) => {
        this.inviteLink.set(result.inviteUrl);
        this.inviteEmail = '';
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to create invite.'), 'Dismiss', { duration: 4000 }),
    });
  }

  removeMember(userId: string, displayName: string): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Remove member',
          message: `Remove ${displayName} from this account? They will lose access immediately.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Remove',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.removeMember(userId).subscribe({
          next: () => this.loadMembers(),
          error: (err) => this.snackbar.open(errorMessage(err, 'Failed to remove member.'), 'Dismiss', { duration: 4000 }),
        });
      });
  }

  approveMember(userId: string): void {
    this.api.approveMember(userId).subscribe({
      next: () => {
        this.snackbar.open('Member approved', 'Dismiss', { duration: 3000 });
        this.loadMembers();
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to approve member.'), 'Dismiss', { duration: 4000 }),
    });
  }

  copyInviteLink(): void {
    const link = this.inviteLink();
    if (link) {
      navigator.clipboard.writeText(link);
      this.snackbar.open('Invite link copied', 'Dismiss', { duration: 2000 });
    }
  }

  deleteAccount(): void {
    const dialogRef = this.dialog.open(ConfirmDeleteDialogComponent, {
      data: {
        title: 'Delete account',
        message: 'This deactivates your account and revokes access for every member. This cannot be undone from the app.',
        confirmWord: 'DELETE',
      },
      width: '420px',
    });

    dialogRef.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;

      this.api.deleteAccount().subscribe({
        next: () => this.auth.logout(),
        error: (err) => this.snackbar.open(errorMessage(err, 'Failed to delete account.'), 'Dismiss', { duration: 4000 }),
      });
    });
  }
}
