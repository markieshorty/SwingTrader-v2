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
        <mat-card class="share-panel">
          <h3>Evidence for your current live settings</h3>
          <p class="intro">
            Sharing unlocks only when an A/B simulation, a <strong>passing</strong> out-of-sample validation and a Monte Carlo
            run all exist for your exact current settings — changing any setting invalidates earlier runs.
            <span class="fingerprint">Fingerprint <code>{{ s.fingerprint.slice(0, 12) }}…</code></span>
          </p>

          <div class="evidence-row" [class.ok]="!!s.sim" [class.missing]="!s.sim">
            <span class="evidence-icon">{{ s.sim ? '\u2713' : '\u2717' }}</span>
            <div class="evidence-body">
              @if (s.sim; as sim) {
                <div class="evidence-title">
                  Historic simulation (A/B run)
                  <span class="evidence-date">{{ sim.completedAt | date: 'medium' }}</span>
                </div>
                <div class="evidence-detail">
                  Full-window replay of these exact settings.
                </div>
                <div class="evidence-stats">
                  <span class="stat-chip">Return {{ sim.totalReturnPct | number: '1.1-1' }}%</span>
                  <span class="stat-chip">SPY {{ sim.spyReturnPct | number: '1.1-1' }}%</span>
                  <span class="stat-chip">{{ sim.trades }} trades</span>
                  <span class="stat-chip">Win rate {{ sim.winRate | percent: '1.0-1' }}</span>
                  <span class="stat-chip">Max DD {{ sim.maxDrawdownPct | number: '1.1-1' }}%</span>
                </div>
              } @else {
                <div class="evidence-title">No historic simulation yet</div>
                <div class="evidence-detail">Run an A/B backtest from the Strategy Lab with your current settings in the user column.</div>
              }
            </div>
          </div>

          <div class="evidence-row" [class.ok]="s.validate?.heldUp" [class.missing]="!s.validate" [class.failed]="s.validate && !s.validate.heldUp">
            <span class="evidence-icon">{{ s.validate?.heldUp ? '✓' : s.validate ? '⚠' : '✗' }}</span>
            <div class="evidence-body">
              @if (s.validate; as v) {
                <div class="evidence-title">
                  Out-of-sample validation {{ v.heldUp ? 'passed' : 'did not hold up' }}
                  <span class="evidence-date">{{ v.completedAt | date: 'medium' }}</span>
                </div>
                <div class="evidence-detail">{{ v.verdict }}</div>
              } @else {
                <div class="evidence-title">No out-of-sample validation yet</div>
                <div class="evidence-detail">Run one from the Strategy Lab A/B tab against your current settings.</div>
              }
            </div>
          </div>

          <div class="evidence-row" [class.ok]="!!s.monteCarlo" [class.missing]="!s.monteCarlo">
            <span class="evidence-icon">{{ s.monteCarlo ? '✓' : '✗' }}</span>
            <div class="evidence-body">
              @if (s.monteCarlo; as m) {
                <div class="evidence-title">
                  Monte Carlo robustness
                  <span class="evidence-date">{{ m.completedAt | date: 'medium' }}</span>
                </div>
                <div class="evidence-detail">{{ m.verdict }}</div>
                <div class="evidence-stats">
                  <span class="stat-chip">Median {{ m.medianTotalReturnPct | number: '1.1-1' }}%</span>
                  <span class="stat-chip">5th pct {{ m.p5TotalReturnPct | number: '1.1-1' }}%</span>
                  <span class="stat-chip">Loss chance {{ m.probabilityOfLossPct | number: '1.1-1' }}%</span>
                </div>
              } @else {
                <div class="evidence-title">No Monte Carlo run yet</div>
                <div class="evidence-detail">Run one from the Strategy Lab against your current settings.</div>
              }
            </div>
          </div>
        </mat-card>

        <mat-card class="share-panel">
          <h3>Send to owners</h3>
          <p class="intro">
            Sends a frozen snapshot of your weights, thresholds, all regime risk books and every setup tactic
            (watchlists stay theirs), with an email quoting the evidence above.
          </p>
          <div class="send-form">
            <mat-form-field appearance="outline">
              <mat-label>Recipients</mat-label>
              <mat-select [(ngModel)]="selectedAccountIds" multiple>
                @for (r of s.recipients; track r.accountId) {
                  <mat-option [value]="r.accountId">{{ r.displayName }} ({{ r.email }})</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Message (optional, included in the email)</mat-label>
              <textarea matInput rows="3" [(ngModel)]="message" maxlength="2000"></textarea>
            </mat-form-field>
            <button mat-raised-button color="primary"
                    [disabled]="!s.canSend || !selectedAccountIds.length || sending()"
                    (click)="send(s)">
              Send strategy
            </button>
            @if (!s.canSend) {
              <p class="hint">Sending is disabled until the evidence above is in place.</p>
            } @else if (!selectedAccountIds.length) {
              <p class="hint">Pick at least one recipient.</p>
            }
          </div>
        </mat-card>

        <mat-card class="share-panel">
          <h3>Sent history</h3>
          @if (!s.history.length) {
            <p class="hint">Nothing sent yet.</p>
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
                    <td>
                      <span class="status-chip" [class.applied]="h.status === 'Applied' && !h.revertedAt">
                        {{ h.revertedAt ? 'Reverted' : h.status }}
                      </span>
                    </td>
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
    :host { display: block; max-width: 860px; }
    .share-panel {
      padding: 20px 24px;
      margin: 16px 0;
      h3 { margin: 0 0 8px; }
    }
    .intro {
      color: var(--st-muted);
      font-size: 13px;
      line-height: 1.5;
      margin: 0 0 16px;
      max-width: 640px;
    }
    .fingerprint { white-space: nowrap; }
    code { font-size: 12px; background: rgba(128, 128, 128, 0.15); border-radius: 4px; padding: 1px 6px; }

    .evidence-row {
      display: flex;
      gap: 12px;
      align-items: flex-start;
      padding: 12px 14px;
      border-radius: 8px;
      background: rgba(128, 128, 128, 0.08);
      border-left: 3px solid var(--st-muted);
      & + .evidence-row { margin-top: 10px; }
      &.ok { border-left-color: var(--st-green, #2e9b57); }
      &.failed { border-left-color: var(--st-amber); }
      &.missing { border-left-color: var(--st-red, #e5484d); }
    }
    .evidence-icon {
      font-size: 16px;
      font-weight: 700;
      line-height: 1.4;
      .ok & { color: var(--st-green, #2e9b57); }
      .failed & { color: var(--st-amber); }
      .missing & { color: var(--st-red, #e5484d); }
    }
    .evidence-body { flex: 1; min-width: 0; }
    .evidence-title {
      font-weight: 600;
      font-size: 14px;
      display: flex;
      align-items: baseline;
      gap: 10px;
      flex-wrap: wrap;
    }
    .evidence-date { color: var(--st-muted); font-size: 12px; font-weight: 400; }
    .evidence-detail { color: var(--st-muted); font-size: 13px; line-height: 1.5; margin-top: 4px; }
    .evidence-stats { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .stat-chip {
      font-size: 12px;
      border-radius: 10px;
      padding: 2px 10px;
      background: rgba(128, 128, 128, 0.15);
    }

    .send-form {
      display: flex;
      flex-direction: column;
      gap: 4px;
      max-width: 480px;
      mat-form-field { width: 100%; }
      button { align-self: flex-start; }
    }
    .hint { color: var(--st-muted); font-size: 12px; margin: 8px 0 0; }

    .history-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
      th, td { text-align: left; padding: 8px 16px 8px 0; }
      th { color: var(--st-muted); font-weight: 500; }
      tbody tr { border-top: 1px solid rgba(128, 128, 128, 0.15); }
    }
    .status-chip {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.03em;
      border-radius: 10px;
      padding: 2px 8px;
      background: rgba(128, 128, 128, 0.15);
      color: var(--st-muted);
      &.applied { background: rgba(46, 155, 87, 0.18); color: var(--st-green, #2e9b57); }
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
