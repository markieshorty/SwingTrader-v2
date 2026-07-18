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
                <p class="pass">
                  ✓ Historic simulation ({{ sim.completedAt | date: 'mediumDate' }}):
                  {{ sim.totalReturnPct | number: '1.1-1' }}% total return
                  (vs SPY {{ sim.spyReturnPct | number: '1.1-1' }}%) over {{ sim.trades }} trades,
                  {{ sim.winRate | percent: '1.0-1' }} win rate,
                  {{ sim.maxDrawdownPct | number: '1.1-1' }}% max drawdown
                </p>
              }
              @if (ev.validate; as v) {
                <p [class.pass]="v.heldUp" [class.warn]="!v.heldUp">
                  {{ v.heldUp ? '✓' : '⚠️' }} Out-of-sample validation
                  {{ v.heldUp ? 'passed' : 'did not hold up' }}
                  ({{ v.completedAt | date: 'mediumDate' }}) — {{ v.verdict }}
                </p>
              }
              @if (ev.monteCarlo; as m) {
                <p>
                  🎲 Monte Carlo ({{ m.completedAt | date: 'mediumDate' }}): {{ m.verdict }} —
                  median return {{ m.medianTotalReturnPct | number: '1.1-1' }}%,
                  5th percentile {{ m.p5TotalReturnPct | number: '1.1-1' }}%,
                  chance of loss {{ m.probabilityOfLossPct | number: '1.1-1' }}%
                </p>
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
              <span class="muted small">Restored your previous settings {{ share.revertedAt | date: 'medium' }}.</span>
            }
          </div>
        </mat-card>
      }
    }
  `,
  styles: [`
    .share-card { margin-bottom: 16px; }
    .header { display: flex; align-items: center; gap: 12px; h3 { margin: 0; } }
    .status-chip {
      font-size: 11px; text-transform: uppercase; letter-spacing: 0.03em;
      border-radius: 10px; padding: 2px 8px; background: rgba(128,128,128,0.15); color: var(--st-muted);
    }
    .status-chip.applied { background: rgba(46,155,87,0.18); color: var(--st-green, #2e9b57); }
    .status-chip.dismissed { opacity: 0.7; }
    .message { font-style: italic; margin: 10px 0 4px; }
    .evidence { margin: 10px 0; font-size: 13px; }
    .evidence .pass { color: var(--st-green, #2e9b57); }
    .evidence .warn { color: var(--st-amber); }
    .snapshot h4 { margin: 8px 0 6px; font-size: 0.85rem; }
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
