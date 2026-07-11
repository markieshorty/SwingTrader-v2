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
import { ConfirmDeleteDialogComponent } from '../../shared/components/confirm-delete-dialog/confirm-delete-dialog.component';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  AccountMemberDto,
  ApiKeyProvider,
  ConnectionState,
  KeyStatusesDto,
  KeyTestResult,
  NotificationRecipientDto,
  RiskProfileDto,
  StrategyWeightsDto,
  TradingMode,
  UpdateRiskProfileDto,
} from '../../core/models/dtos';
import { readTabIndexFromRoute, writeTabIndexToRoute } from '../../shared/utils/tab-route.util';
import { errorMessage } from '../../shared/utils/error-message.util';

const TAB_NAMES = ['api-keys', 'trading', 'strategy', 'risk', 'notifications', 'account'] as const;

const COMPONENT_WEIGHT_FIELDS: { key: keyof StrategyWeightsDto; label: string }[] = [
  { key: 'rsiWeight', label: 'RSI' },
  { key: 'macdWeight', label: 'MACD' },
  { key: 'volumeWeight', label: 'Volume' },
  { key: 'sentimentWeight', label: 'Sentiment' },
  { key: 'setupQualityWeight', label: 'Setup Quality' },
  { key: 'relativeStrengthWeight', label: 'Relative Strength' },
  { key: 'priceLevelWeight', label: 'Price Level' },
  { key: 'fundamentalMomentumWeight', label: 'Fundamental Momentum' },
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
    maxPositionPctOfActive: profile.maxPositionPctOfActive,
    maxOpenPositions: profile.maxOpenPositions,
    dailyLossCircuitBreakerPct: profile.dailyLossCircuitBreakerPct,
    tier1UnlockMinTrades: profile.tier1UnlockMinTrades,
    tier1UnlockMinWinRate: profile.tier1UnlockMinWinRate,
    tier2UnlockMinTrades: profile.tier2UnlockMinTrades,
    tier2UnlockMinWinRate: profile.tier2UnlockMinWinRate,
    maxHoldDays: profile.maxHoldDays,
    trailingActivationPct: profile.trailingActivationPct,
    trailingDistancePct: profile.trailingDistancePct,
    earningsGateDays: profile.earningsGateDays,
    minHoldDays: profile.minHoldDays,
    momentumHealthThreshold: profile.momentumHealthThreshold,
    targetWatchlistSize: profile.targetWatchlistSize,
    autopauseDuringBear: profile.autopauseDuringBear,
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

  weightsSum = computed(() => {
    const w = this.weights();
    if (!w) return 0;
    return COMPONENT_WEIGHT_FIELDS.reduce((sum, f) => sum + (Number(w[f.key]) || 0), 0);
  });

  weightsSumValid = computed(() => Math.abs(this.weightsSum() - 1) < 0.001);

  riskProfile = signal<RiskProfileDto | null>(null);
  // Working copy the sliders bind to - saved explicitly via saveRiskProfile(),
  // so a user can cancel/reload without their in-progress drag committing.
  riskProfileDraft = signal<UpdateRiskProfileDto | null>(null);

  riskProfileDirty = computed(() => {
    const original = this.riskProfile();
    const draft = this.riskProfileDraft();
    if (!original || !draft) return false;
    return (
      original.lockedCapitalPct !== draft.lockedCapitalPct ||
      original.maxPositionPctOfActive !== draft.maxPositionPctOfActive ||
      original.maxOpenPositions !== draft.maxOpenPositions ||
      original.dailyLossCircuitBreakerPct !== draft.dailyLossCircuitBreakerPct ||
      original.tier1UnlockMinTrades !== draft.tier1UnlockMinTrades ||
      original.tier1UnlockMinWinRate !== draft.tier1UnlockMinWinRate ||
      original.tier2UnlockMinTrades !== draft.tier2UnlockMinTrades ||
      original.tier2UnlockMinWinRate !== draft.tier2UnlockMinWinRate ||
      original.maxHoldDays !== draft.maxHoldDays ||
      original.trailingActivationPct !== draft.trailingActivationPct ||
      original.trailingDistancePct !== draft.trailingDistancePct ||
      original.earningsGateDays !== draft.earningsGateDays ||
      original.minHoldDays !== draft.minHoldDays ||
      original.momentumHealthThreshold !== draft.momentumHealthThreshold ||
      original.targetWatchlistSize !== draft.targetWatchlistSize ||
      original.autopauseDuringBear !== draft.autopauseDuringBear
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

  // Template helper (Math isn't accessible in Angular templates): the Tier 2
  // sliders use this to keep their min above the current Tier 1 draft value,
  // enforcing the "Tier 2 must exceed Tier 1" rule visually, not just at save.
  max(a: number, b: number): number {
    return Math.max(a, b);
  }

  riskLivePreview = computed(() => {
    const draft = this.riskProfileDraft();
    const breakdown = this.riskProfile()?.capitalBreakdown;
    if (!draft) return null;
    // Active capital is set by the account's TIER (10% / 20% / 50% of the
    // whole account at Tier 1 / 2 / 3), NOT by (total − locked). Locked
    // capital is a protected reserve and a ceiling on max-position %; it does
    // not shrink the tier pool that positions are actually sized from. Use the
    // API's tier-based figures so this preview matches live sizing exactly
    // (the old total−locked calc overstated it ~4× at Tier 1).
    const total = breakdown?.totalCapital ?? null;
    const active = breakdown?.activeCapital ?? null; // the tier pool
    const tier = breakdown?.currentTier ?? null;
    const locked = total !== null ? total * draft.lockedCapitalPct : null;
    const activePct = total && active ? active / total : null;
    const maxPerTrade = active !== null ? active * draft.maxPositionPctOfActive : null;
    return { total, locked, active, activePct, tier, maxPerTrade };
  });

  constructor() {
    this.loadKeyStatuses();
    this.loadAccountSettings();
    this.loadRecipients();
    this.loadMembers();
    this.loadWeights();
    this.loadRiskProfile();
    this.api.getMe().subscribe({ next: (me) => this.me.set({ email: me.email, displayName: me.displayName }) });
    this.selectedTabIndex.set(readTabIndexFromRoute(this.route, TAB_NAMES));
  }

  private loadRiskProfile(): void {
    this.api.getRiskProfile().subscribe({
      next: (profile) => {
        this.riskProfile.set(profile);
        this.riskProfileDraft.set(toUpdateRiskProfileDto(profile));
      },
      error: () => {
        this.riskProfile.set(null);
        this.riskProfileDraft.set(null);
      },
    });
  }

  updateRiskDraftField(key: keyof UpdateRiskProfileDto, value: number | boolean): void {
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

  // Saves immediately rather than waiting on the Risk Management tab's
  // explicit Save button - it lives next to the manual pause toggle in
  // Trading, and a toggle sitting next to an immediate-acting control
  // shouldn't itself require a trip to another tab to take effect.
  toggleAutopauseDuringBear(checked: boolean): void {
    const draft = this.riskProfileDraft();
    if (!draft) return;
    // Can only be turned ON while manual pause is off - the toggle is
    // disabled in the template for this case too, but guard here as well
    // since this method also drives the auto-off when manual pause engages.
    if (checked && this.executionPaused()) return;
    const updated = { ...draft, autopauseDuringBear: checked };
    this.riskProfileDraft.set(updated);
    this.api.updateRiskProfile(updated).subscribe({
      next: () => {
        this.riskProfile.set(this.riskProfile() ? { ...this.riskProfile()!, autopauseDuringBear: checked } : null);
        this.snackbar.open(
          checked ? 'Bear-market autopause enabled' : 'Bear-market autopause disabled',
          'Dismiss', { duration: 3000 });
      },
      error: (err) => {
        this.riskProfileDraft.set({ ...updated, autopauseDuringBear: !checked }); // revert
        this.snackbar.open(errorMessage(err, 'Failed to update.'), 'Dismiss', { duration: 4000 });
      },
    });
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
          title: 'Reset risk profile',
          message: 'Reset risk profile to defaults? Your current settings will be lost.',
          cancelLabel: 'Cancel',
          confirmLabel: 'Reset',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.resetRiskProfile().subscribe({
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
      this.snackbar.open(`Component weights must sum to 1.0 — currently ${this.weightsSum().toFixed(3)}.`, 'Dismiss', { duration: 5000 });
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

    // Manual and auto pause can't both be "in control" at once - turning on
    // manual pause takes precedence and turns bear-autopause off, rather than
    // leaving it silently armed underneath a pause the user just set by hand.
    if (paused && this.riskProfileDraft()?.autopauseDuringBear) {
      this.toggleAutopauseDuringBear(false);
    }

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
