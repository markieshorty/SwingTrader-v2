import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/services/api.service';
import { StrategyShareDto } from '../../core/models/dtos';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/components/confirm-dialog/confirm-dialog.component';
import { errorMessage } from '../../shared/utils/error-message.util';

// Strategies other owners have shared with this account. Each card shows the
// frozen snapshot's headline settings + the evidence tied to it (out-of-sample
// validation + Monte Carlo, fingerprint-matched at send time). Apply
// overwrites weights, all regime risk books and all setup tactics - after an
// automatic backup that "Restore my previous settings" puts back.
@Component({
  selector: 'app-shared-strategies',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatButtonModule, MatIconModule, MatTooltipModule, LoadingSpinnerComponent],
  template: `
    @if (!loaded()) {
      <app-loading-spinner />
    } @else if (!shares().length) {
      <mat-card class="panel"><p class="muted">No strategies have been shared with you yet.</p></mat-card>
    } @else {
      @for (share of shares(); track share.id) {
        <mat-card class="panel share-card">
          <div class="header">
            <h3>From {{ share.senderName }}</h3>
            <span class="status-chip" [class]="share.status.toLowerCase()">{{ statusLabel(share) }}</span>
            <span class="muted small">Sent {{ share.sentAt | date: 'medium' }}</span>
          </div>

          @if (share.message) {
            <p class="message">“{{ share.message }}”</p>
          }

          @if (share.evidence; as ev) {
            <div class="evidence">
              @if (ev.sim; as sim) {
                <div class="evidence-row ok">
                  <span class="evidence-icon">✓</span>
                  <div class="evidence-body">
                    <div class="evidence-title">
                      Historic simulation
                      <span class="evidence-date">{{ sim.completedAt | date: 'medium' }}</span>
                    </div>
                    <div class="evidence-detail">Full-window replay of exactly these settings under {{ share.senderName }}'s live regime setup.</div>
                    <div class="evidence-stats">
                      <span class="stat-chip">Return {{ sim.totalReturnPct | number: '1.1-1' }}%</span>
                      <span class="stat-chip">SPY {{ sim.spyReturnPct | number: '1.1-1' }}%</span>
                      <span class="stat-chip">{{ sim.trades }} trades</span>
                      <span class="stat-chip">Win rate {{ sim.winRate | percent: '1.0-1' }}</span>
                      <span class="stat-chip">Max DD {{ sim.maxDrawdownPct | number: '1.1-1' }}%</span>
                    </div>
                  </div>
                </div>
              }
              @if (ev.validate; as v) {
                <div class="evidence-row" [class.ok]="v.heldUp" [class.failed]="!v.heldUp">
                  <span class="evidence-icon">{{ v.heldUp ? '✓' : '⚠️' }}</span>
                  <div class="evidence-body">
                    <div class="evidence-title">
                      Out-of-sample validation {{ v.heldUp ? 'passed' : 'did not hold up' }}
                      <span class="evidence-date">{{ v.completedAt | date: 'medium' }}</span>
                    </div>
                    <div class="evidence-detail">{{ v.verdict }}</div>
                  </div>
                </div>
              }
              @if (ev.monteCarlo; as m) {
                <div class="evidence-row ok">
                  <span class="evidence-icon">✓</span>
                  <div class="evidence-body">
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
                  </div>
                </div>
              }
              <p class="muted small">
                This evidence is fingerprint-tied to exactly these settings — it was produced by the
                configuration below, not an earlier version.
              </p>
            </div>
          }

          @if (share.snapshot; as snap) {
            <div class="snapshot">
              <h4>What you'd be applying</h4>
              <div class="chips">
                <span class="chip">Buy ≥ {{ snap.weights.buyThreshold | number: '1.1-1' }}</span>
                <span class="chip">Watch ≥ {{ snap.weights.watchThreshold | number: '1.1-1' }}</span>
                <span class="chip">
                  Gate: RSI {{ snap.weights.rsiWeight | percent: '1.0-0' }} ·
                  MACD {{ snap.weights.macdWeight | percent: '1.0-0' }} ·
                  Vol {{ snap.weights.volumeWeight | percent: '1.0-0' }} ·
                  Setup {{ snap.weights.setupQualityWeight | percent: '1.0-0' }} ·
                  RS {{ snap.weights.relativeStrengthWeight | percent: '1.0-0' }} ·
                  Price {{ snap.weights.priceLevelWeight | percent: '1.0-0' }}
                </span>
                <span class="chip">{{ snap.riskBooks.length }} risk books</span>
                <span class="chip">
                  Setups on: {{ enabledSetups(share) }}
                </span>
                @if (disabledSetups(share); as off) {
                  <span class="chip off">Setups off: {{ off }}</span>
                }
              </div>
              <p class="muted small">
                Covers strategy weights &amp; thresholds, all regime risk books (autopause, sizing, veto floor…)
                and every per-setup tactic. Watchlists are not included.
              </p>
            </div>
          }

          <div class="actions">
            @if (share.status === 'Sent') {
              <button mat-raised-button color="primary" [disabled]="busy()" (click)="apply(share)">
                Apply to live
              </button>
              <button mat-stroked-button [disabled]="busy()" (click)="dismiss(share)">Dismiss</button>
            }
            @if (share.canRevert) {
              <button mat-stroked-button color="warn" [disabled]="busy()" (click)="revert(share)">
                Restore my previous settings
              </button>
            }
            @if (share.status === 'Applied' && !share.revertedAt) {
              <span class="muted small verify-nudge">
                Applied {{ share.appliedAt | date: 'medium' }}.
                <a routerLink="/strategy-lab">Verify with your own backtest</a> — on the shared candle data
                it should reproduce {{ share.senderName }}'s results (tiny drift possible if new market days
                have synced since their runs).
              </span>
            }
            @if (share.revertedAt) {
              <button mat-raised-button color="primary" [disabled]="busy()" (click)="apply(share)">
                Apply to live again
              </button>
              <span class="muted small">
                Restored your previous settings {{ share.revertedAt | date: 'medium' }} — you can re-apply
                the shared settings any time (a fresh backup is taken).
              </span>
            }
          </div>
        </mat-card>
      }
    }
  `,
  styles: [`
    .share-card { padding: 20px 24px; margin-bottom: 16px; }
    .header { display: flex; align-items: center; gap: 12px; h3 { margin: 0; } }
    .evidence-row {
      display: flex; gap: 12px; align-items: flex-start;
      padding: 12px 14px; border-radius: 8px;
      background: rgba(128, 128, 128, 0.08);
      border-left: 3px solid var(--st-muted);
      & + .evidence-row { margin-top: 10px; }
      &.ok { border-left-color: var(--st-green, #2e9b57); }
      &.failed { border-left-color: var(--st-amber); }
    }
    .evidence-icon {
      font-size: 16px; font-weight: 700; line-height: 1.4;
      .ok & { color: var(--st-green, #2e9b57); }
      .failed & { color: var(--st-amber); }
    }
    .evidence-body { flex: 1; min-width: 0; }
    .evidence-title {
      font-weight: 600; font-size: 14px;
      display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap;
    }
    .evidence-date { color: var(--st-muted); font-size: 12px; font-weight: 400; }
    .evidence-detail { color: var(--st-muted); font-size: 13px; line-height: 1.5; margin-top: 4px; }
    .evidence-stats { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .stat-chip { font-size: 12px; border-radius: 10px; padding: 2px 10px; background: rgba(128, 128, 128, 0.15); }
    .status-chip {
      font-size: 11px; text-transform: uppercase; letter-spacing: 0.03em;
      border-radius: 10px; padding: 2px 8px; background: rgba(128,128,128,0.15); color: var(--st-muted);
    }
    .status-chip.applied { background: rgba(46,155,87,0.18); color: var(--st-green, #2e9b57); }
    .status-chip.dismissed { opacity: 0.7; }
    .message { font-style: italic; margin: 10px 0 4px; }
    .evidence { margin: 14px 0; }
    .snapshot h4 { margin: 12px 0 8px; font-size: 0.9rem; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; }
    .chip { font-size: 12px; border-radius: 10px; padding: 2px 10px; background: rgba(128,128,128,0.15); }
    .chip.off { background: rgba(217,119,6,0.15); color: var(--st-amber); }
    .actions { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-top: 12px; }
    .muted { color: var(--st-muted); }
    .small { font-size: 12px; }
    .verify-nudge a { color: inherit; text-decoration: underline; }
  `],
})
export class SharedStrategiesComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);

  loaded = signal(false);
  busy = signal(false);
  shares = signal<StrategyShareDto[]>([]);

  constructor() {
    this.load();
  }

  statusLabel(share: StrategyShareDto): string {
    if (share.revertedAt) return 'Reverted';
    return share.status === 'Sent' ? 'New' : share.status;
  }

  enabledSetups(share: StrategyShareDto): string {
    return (share.snapshot?.setupTactics ?? []).filter((t) => t.enabled).map((t) => t.setupType).join(', ') || '—';
  }

  disabledSetups(share: StrategyShareDto): string | null {
    const off = (share.snapshot?.setupTactics ?? []).filter((t) => !t.enabled).map((t) => t.setupType);
    return off.length ? off.join(', ') : null;
  }

  apply(share: StrategyShareDto): void {
    this.confirm({
      title: `Apply ${share.senderName}'s strategy to your live settings?`,
      message:
        `This OVERWRITES your strategy weights and thresholds, all regime risk books and every ` +
        `per-setup tactic with ${share.senderName}'s shared settings (watchlists are untouched). ` +
        `A backup of your current settings is taken automatically first — you can restore it with one click.`,
      cancelLabel: 'Cancel',
      confirmLabel: 'Apply to live',
      confirmColor: 'warn',
    }, () => this.api.applyStrategyShare(share.id), 'Strategy applied — backup of your previous settings saved.');
  }

  dismiss(share: StrategyShareDto): void {
    this.confirm({
      title: 'Dismiss this shared strategy?',
      message: `It won't be applied and drops off your pending count. The card stays here for reference.`,
      cancelLabel: 'Cancel',
      confirmLabel: 'Dismiss',
      confirmColor: 'warn',
    }, () => this.api.dismissStrategyShare(share.id), 'Share dismissed.');
  }

  revert(share: StrategyShareDto): void {
    this.confirm({
      title: 'Restore your previous settings?',
      message:
        `This puts back the weights, risk books and setup tactics you had immediately before applying ` +
        `${share.senderName}'s strategy.`,
      cancelLabel: 'Cancel',
      confirmLabel: 'Restore',
      confirmColor: 'warn',
    }, () => this.api.revertStrategyShare(share.id), 'Your previous settings are restored.');
  }

  private confirm(
    data: ConfirmDialogData,
    action: () => ReturnType<ApiService['applyStrategyShare']>,
    successMessage: string,
  ): void {
    const ref = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '440px',
      data,
    });
    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.busy.set(true);
      action().subscribe({
        next: () => {
          this.snackbar.open(successMessage, 'Dismiss', { duration: 5000 });
          this.busy.set(false);
          this.load();
        },
        error: (err) => {
          this.busy.set(false);
          this.snackbar.open(errorMessage(err, 'The action failed.'), 'Dismiss', { duration: 5000 });
        },
      });
    });
  }

  private load(): void {
    this.api.getStrategyShares().subscribe({
      next: (shares) => {
        this.shares.set(shares);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(true),
    });
  }
}
