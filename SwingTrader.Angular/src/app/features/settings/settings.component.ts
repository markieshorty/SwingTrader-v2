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
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import {
  AccountMemberDto,
  ApiKeyProvider,
  KeyStatusesDto,
  NotificationRecipientDto,
  TradingMode,
} from '../../core/models/dtos';

const PROVIDER_LABELS: Record<ApiKeyProvider, string> = {
  Finnhub: 'Finnhub',
  Tiingo: 'Tiingo',
  Trading212DemoKey: 'Trading 212 API Key (Demo)',
  Trading212DemoSecret: 'Trading 212 API Secret (Demo)',
  Trading212LiveKey: 'Trading 212 API Key (Live)',
  Trading212LiveSecret: 'Trading 212 API Secret (Live)',
  Claude: 'Claude (Anthropic)',
  EmailUsername: 'Email Username',
  EmailPassword: 'Email Password',
};

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
  ],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  auth = inject(AuthService);

  providers = Object.keys(PROVIDER_LABELS) as ApiKeyProvider[];
  providerLabel = (p: ApiKeyProvider) => PROVIDER_LABELS[p];

  keyStatuses = signal<KeyStatusesDto | null>(null);
  editingProvider = signal<ApiKeyProvider | null>(null);
  keyInput = '';

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

  recipients = signal<NotificationRecipientDto[]>([]);
  newRecipientEmail = '';

  members = signal<AccountMemberDto[]>([]);
  inviteEmail = '';
  inviteLink = signal<string | null>(null);

  constructor() {
    this.loadKeyStatuses();
    this.loadAccountSettings();
    this.loadRecipients();
    this.loadMembers();
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

  addRecipient(): void {
    if (!this.newRecipientEmail.trim()) return;
    this.api.addNotificationRecipient(this.newRecipientEmail.trim(), 31 /* NotificationCategory.All */).subscribe({
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

  copyInviteLink(): void {
    const link = this.inviteLink();
    if (link) {
      navigator.clipboard.writeText(link);
      this.snackbar.open('Invite link copied', 'Dismiss', { duration: 2000 });
    }
  }

  deleteAccount(): void {
    if (!confirm('This deactivates your account and revokes access for every member. This cannot be undone from the app. Continue?')) return;

    this.api.deleteAccount().subscribe({
      next: () => this.auth.logout(),
      error: (err) => {
        const message = err.status === 403 ? 'Only the account Owner can delete the account.' : 'Failed to delete account.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }
}
