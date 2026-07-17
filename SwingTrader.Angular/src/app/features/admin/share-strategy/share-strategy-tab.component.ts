import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../../core/services/api.service';
import { ShareAdminStatusDto } from '../../../core/models/dtos';
import { LoadingSpinnerComponent } from '../../../shared/components/loading-spinner/loading-spinner.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { errorMessage } from '../../../shared/utils/error-message.util';

// Admin "Share Strategy" tab: shows whether the admin's CURRENT live settings
// have fingerprint-tied evidence (a passing out-of-sample validation + a
// Monte Carlo run of exactly this config), and if so lets them send a frozen
// snapshot to other account owners with an email. Send is disabled until the
// evidence exists - and re-checked server-side.
@Component({
  selector: 'app-share-strategy-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, MatCardModule, MatButtonModule, MatFormFieldModule,
    MatInputModule, MatSelectModule, LoadingSpinnerComponent],
  template: `
    @if (!loaded()) {
      <app-loading-spinner />
    } @else {
      @if (status(); as s) {
      <mat-card class="panel">
        <h3>Evidence for your current live settings</h3>
        <p class="muted small">
          Fingerprint <code>{{ s.fingerprint.slice(0, 12) }}…</code> — computed from your live weights,
          thresholds, active risk book and setup tactics. Sharing unlocks only when a PASSING out-of-sample
          validation and a Monte Carlo run exist for this exact fingerprint; changing any setting invalidates
          earlier runs.
        </p>
        <p [class.pass]="s.validate?.heldUp" [class.warn]="!s.validate?.heldUp">
          @if (s.validate; as v) {
            {{ v.heldUp ? '✓ Out-of-sample validation PASSED' : '⚠️ Validation ran but did NOT hold up' }}
            ({{ v.completedAt | date: 'medium' }}) — {{ v.verdict }}
          } @else {
            ✗ No out-of-sample validation for the current settings — run one from the Strategy Lab A/B tab.
          }
        </p>
        <p [class.pass]="!!s.monteCarlo" [class.warn]="!s.monteCarlo">
          @if (s.monteCarlo; as m) {
            ✓ Monte Carlo ({{ m.completedAt | date: 'medium' }}) — {{ m.verdict }}:
            median {{ m.medianTotalReturnPct | number: '1.1-1' }}%,
            5th pct {{ m.p5TotalReturnPct | number: '1.1-1' }}%,
            loss chance {{ m.probabilityOfLossPct | number: '1.1-1' }}%
          } @else {
            ✗ No Monte Carlo run for the current settings — run one from the Strategy Lab.
          }
        </p>
      </mat-card>

      <mat-card class="panel">
        <h3>Send to owners</h3>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Recipients</mat-label>
          <mat-select [(ngModel)]="selectedAccountIds" multiple>
            @for (r of s.recipients; track r.accountId) {
              <mat-option [value]="r.accountId">{{ r.displayName }} ({{ r.email }})</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Message (optional, included in the email)</mat-label>
          <textarea matInput rows="3" [(ngModel)]="message" maxlength="2000"></textarea>
        </mat-form-field>
        <button mat-raised-button color="primary"
                [disabled]="!s.canSend || !selectedAccountIds.length || sending()"
                (click)="send(s)">
          Send strategy
        </button>
        @if (!s.canSend) {
          <p class="muted small">Sending is disabled until the evidence above is in place.</p>
        }
      </mat-card>

      <mat-card class="panel">
        <h3>Sent history</h3>
        @if (!s.history.length) {
          <p class="muted">Nothing sent yet.</p>
        } @else {
          <table class="history-table">
            <thead>
              <tr><th>Sent</th><th>Recipient</th><th>Status</th><th>Applied</th></tr>
            </thead>
            <tbody>
              @for (h of s.history; track h.id) {
                <tr>
                  <td>{{ h.sentAt | date: 'medium' }}</td>
                  <td>{{ h.recipientName }}</td>
                  <td>{{ h.revertedAt ? 'Reverted' : h.status }}</td>
                  <td>{{ h.appliedAt ? (h.appliedAt | date: 'medium') : '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </mat-card>
      }
    }
  `,
  styles: [`
    .full { width: 100%; }
    .pass { color: var(--st-green, #2e9b57); }
    .warn { color: var(--st-amber); }
    .muted { color: var(--st-muted); }
    .small { font-size: 12px; }
    code { font-size: 12px; }
    .history-table { width: 100%; border-collapse: collapse; font-size: 13px;
      th, td { text-align: left; padding: 6px 12px 6px 0; }
      th { color: var(--st-muted); font-weight: 500; }
    }
  `],
})
export class ShareStrategyTabComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);

  loaded = signal(false);
  sending = signal(false);
  status = signal<ShareAdminStatusDto | null>(null);

  selectedAccountIds: number[] = [];
  message = '';

  constructor() {
    this.load();
  }

  send(s: ShareAdminStatusDto): void {
    const names = s.recipients
      .filter((r) => this.selectedAccountIds.includes(r.accountId))
      .map((r) => r.displayName);
    const ref = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '440px',
      data: {
        title: `Send your strategy to ${names.length} owner${names.length === 1 ? '' : 's'}?`,
        message:
          `A frozen snapshot of your current settings (weights, all risk books, all setup tactics) will be ` +
          `sent to: ${names.join(', ')}. Each gets an email with the validation and Monte Carlo verdicts and ` +
          `a link to review and apply it.`,
        cancelLabel: 'Cancel',
        confirmLabel: 'Send emails',
        confirmColor: 'warn',
      },
    });
    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.sending.set(true);
      this.api.sendStrategyShare(this.selectedAccountIds, this.message.trim() || null).subscribe({
        next: (r) => {
          this.sending.set(false);
          this.selectedAccountIds = [];
          this.message = '';
          this.snackbar.open(`Sent to ${r.sent.length} recipient(s).`, 'Dismiss', { duration: 5000 });
          this.load();
        },
        error: (err) => {
          this.sending.set(false);
          this.snackbar.open(errorMessage(err, 'Send failed.'), 'Dismiss', { duration: 6000 });
        },
      });
    });
  }

  private load(): void {
    this.api.getShareAdminStatus().subscribe({
      next: (s) => {
        this.status.set(s);
        this.loaded.set(true);
      },
      error: (err) => {
        this.loaded.set(true);
        this.snackbar.open(errorMessage(err, 'Failed to load share status.'), 'Dismiss', { duration: 6000 });
      },
    });
  }
}
