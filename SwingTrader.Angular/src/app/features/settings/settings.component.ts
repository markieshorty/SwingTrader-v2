import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatDialog } from '@angular/material/dialog';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { ConfirmDeleteDialogComponent } from '../../shared/components/confirm-delete-dialog/confirm-delete-dialog.component';
import {
  AccountMemberDto,
  ApiKeyProvider,
  KeyStatusesDto,
  NotificationRecipientDto,
  RiskProfileDto,
  StrategyWeightsDto,
  TradingMode,
  UpdateRiskProfileDto,
} from '../../core/models/dtos';

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
  ],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  private dialog = inject(MatDialog);
  auth = inject(AuthService);

  providers = Object.keys(PROVIDER_LABELS) as ApiKeyProvider[];
  providerLabel = (p: ApiKeyProvider) => PROVIDER_LABELS[p];

  keyStatuses = signal<KeyStatusesDto | null>(null);
  editingProvider = signal<ApiKeyProvider | null>(null);
  keyInput = '';

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
  approvalRequired = signal(true);
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
      original.tier2UnlockMinWinRate !== draft.tier2UnlockMinWinRate
    );
  });

  riskLivePreview = computed(() => {
    const draft = this.riskProfileDraft();
    const breakdown = this.riskProfile()?.capitalBreakdown;
    const total = breakdown?.totalCapital ?? 1000;
    if (!draft) return null;
    const locked = total * draft.lockedCapitalPct;
    const active = total - locked;
    const maxPerTrade = active * draft.maxPositionPctOfActive;
    return { total, locked, active, maxPerTrade };
  });

  constructor() {
    this.loadKeyStatuses();
    this.loadAccountSettings();
    this.loadRecipients();
    this.loadMembers();
    this.loadWeights();
    this.loadRiskProfile();
    this.api.getMe().subscribe({ next: (me) => this.me.set({ email: me.email, displayName: me.displayName }) });
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

  updateRiskDraftField(key: keyof UpdateRiskProfileDto, value: number): void {
    const draft = this.riskProfileDraft();
    if (!draft) return;
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
      error: (err) => {
        const message =
          err.status === 403
            ? 'Only the account Owner can change the risk profile.'
            : (err.error?.message ?? 'Failed to save.');
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  resetRiskProfile(): void {
    this.api.resetRiskProfile().subscribe({
      next: () => {
        this.snackbar.open('Risk profile reset to defaults', 'Dismiss', { duration: 3000 });
        this.loadRiskProfile();
      },
      error: (err) => {
        const message = err.status === 403 ? 'Only the account Owner can reset the risk profile.' : 'Failed to reset.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
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
      error: (err) => {
        const message =
          err.status === 403
            ? 'Only the account Owner can change strategy weights.'
            : (err.error?.message ?? 'Failed to save.');
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
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
        this.approvalRequired.set(settings.approvalRequired);
        this.t212AccountId.set(settings.t212AccountId);
        this.globalRefinementOptIn.set(settings.globalRefinementOptIn);
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

  saveAndTestKey(): void {
    const provider = this.editingProvider();
    if (!provider || !this.keyInput.trim()) return;

    this.api.saveKey(provider, this.keyInput.trim()).subscribe({
      next: (result) => {
        this.snackbar.open(result.valid ? 'Key saved and verified' : 'Key saved but could not be verified', 'Dismiss', { duration: 4000 });
        this.cancelEditing();
        this.loadKeyStatuses();
      },
      error: () => this.snackbar.open('Failed to save key', 'Dismiss', { duration: 4000 }),
    });
  }

  testKey(provider: ApiKeyProvider): void {
    this.api.testKey(provider).subscribe({
      next: (result) => {
        this.snackbar.open(result.valid ? 'Key is valid' : 'Key test failed', 'Dismiss', { duration: 4000 });
        this.loadKeyStatuses();
      },
    });
  }

  removeKey(provider: ApiKeyProvider): void {
    this.api.deleteKey(provider).subscribe({ next: () => this.loadKeyStatuses() });
  }

  saveTradingConfig(): void {
    this.api.updateTradingConfig(this.tradingMode(), this.approvalRequired()).subscribe({
      next: () => this.snackbar.open('Trading settings saved', 'Dismiss', { duration: 3000 }),
      error: (err) => {
        const message =
          err.status === 403
            ? 'Only the account Owner can change trading settings.'
            : (err.error?.message ?? 'Failed to save.');
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  toggleGlobalRefinement(enabled: boolean): void {
    this.globalRefinementOptIn.set(enabled);
    this.api.setGlobalRefinementOptIn(enabled).subscribe();
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
      error: (err) => {
        const message = err.status === 403 ? 'Only the account Owner can manage notifications.' : 'Failed to add recipient.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  removeRecipient(id: number): void {
    this.api.removeNotificationRecipient(id).subscribe({ next: () => this.loadRecipients() });
  }

  toggleTradeApproval(recipient: NotificationRecipientDto, enabled: boolean): void {
    this.api.setTradeApproval(recipient.id, enabled).subscribe({
      next: () => this.loadRecipients(),
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
      error: (err) => {
        const message = err.status === 403 ? 'Only the account Owner can invite members.' : 'Failed to create invite.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  removeMember(userId: string): void {
    this.api.removeMember(userId).subscribe({
      next: () => this.loadMembers(),
      error: (err) => {
        const message = err.status === 403 ? 'Only the account Owner can remove members.' : 'Failed to remove member.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  approveMember(userId: string): void {
    this.api.approveMember(userId).subscribe({
      next: () => {
        this.snackbar.open('Member approved', 'Dismiss', { duration: 3000 });
        this.loadMembers();
      },
      error: (err) => {
        const message = err.status === 403 ? 'Only the account Owner can approve members.' : 'Failed to approve member.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
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
        error: (err) => {
          const message = err.status === 403 ? 'Only the account Owner can delete the account.' : 'Failed to delete account.';
          this.snackbar.open(message, 'Dismiss', { duration: 4000 });
        },
      });
    });
  }
}
