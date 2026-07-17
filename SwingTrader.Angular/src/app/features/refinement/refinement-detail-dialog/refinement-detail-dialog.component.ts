import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../../core/services/api.service';
import { WeightEditorComponent } from '../weight-editor/weight-editor.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { RefinementSuggestionDto } from '../../../core/models/dtos';
import { errorMessage } from '../../../shared/utils/error-message.util';

// Opened from a refinement-history row: shows the run's stats + its weights
// (current vs suggested) and lets the owner re-apply those weights to live.
// Applying always goes through a confirm step.
@Component({
  selector: 'app-refinement-detail-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, WeightEditorComponent],
  template: `
    <h2 mat-dialog-title>Refinement run — {{ s.generatedAt | date: 'medium' }}</h2>
    <mat-dialog-content>
      <div class="chips">
        <span class="confidence" [class]="s.confidenceLevel.toLowerCase()">{{ s.confidenceLevel }} confidence</span>
        <span class="origin-chip" [class.lab]="s.origin === 'StrategyLab'">
          {{ s.origin === 'StrategyLab' ? 'Strategy Lab' : 'Auto refinement' }}
        </span>
        <span class="status-chip" [class]="s.status.toLowerCase()">{{ s.status }}</span>
      </div>

      <p class="period">
        Analysis window: {{ s.analysisPeriodStart | date: 'mediumDate' }} – {{ s.analysisPeriodEnd | date: 'mediumDate' }}
      </p>

      <div class="stat-grid">
        <div><span class="k">Trades analysed</span><span class="v">{{ s.tradeCountAnalysed }}</span></div>
        <div><span class="k">Winners / losers</span><span class="v">{{ s.winnerCount }} / {{ s.loserCount }}</span></div>
        <div><span class="k">Win rate</span><span class="v">{{ s.overallWinRate | percent: '1.0-1' }}</span></div>
        <div><span class="k">Market-adjusted</span><span class="v">{{ s.marketAdjustedWinRate | number: '1.0-1' }}%</span></div>
      </div>

      @if (s.unusualMarketConditions && s.marketConditionWarning) {
        <p class="warning">⚠️ {{ s.marketConditionWarning }}</p>
      }

      @if (s.replayCheckPassed !== null && s.replayCheckPassed !== undefined) {
        <p class="replay" [class.pass]="s.replayCheckPassed" [class.fail]="!s.replayCheckPassed">
          {{ s.replayCheckPassed ? '✓ Replay check passed' : '⚠️ Replay check FAILED' }} —
          suggested {{ s.replaySuggestedAvgReturnPct | number: '1.2-2' }}%/trade vs
          current {{ s.replayCurrentAvgReturnPct | number: '1.2-2' }}% over {{ s.replayTradesKept }} kept trades.
        </p>
      }

      @if (s.assessmentSummary) {
        <p class="assessment">{{ s.assessmentSummary }}</p>
      }

      <h4>Weights</h4>
      <app-weight-editor [currentWeights]="s.currentWeights" [suggestedWeights]="s.suggestedWeights" />

      @if (s.suggestedRiskRules; as rr) {
        <h4>Risk settings in this apply — {{ rr.targetRegime }} book</h4>
        <div class="risk-chips">
          @if (rr.autopause !== null) { <span class="risk-chip">Autopause: {{ rr.autopause ? 'ON' : 'OFF' }}</span> }
          @if (rr.rules?.stopLossPct != null) { <span class="risk-chip">Stop {{ rr.rules!.stopLossPct! | percent: '1.0-1' }}</span> }
          @if (rr.rules?.targetPct != null) { <span class="risk-chip">Target {{ rr.rules!.targetPct! | percent: '1.0-1' }}</span> }
          @if (rr.rules?.maxHoldDays != null) { <span class="risk-chip">Guide hold {{ rr.rules!.maxHoldDays }}d</span> }
          @if (rr.rules?.trailingActivationPct != null) { <span class="risk-chip">Trail arm +{{ rr.rules!.trailingActivationPct! | percent: '1.0-1' }}</span> }
          @if (rr.rules?.trailingDistancePct != null) { <span class="risk-chip">Trail dist {{ rr.rules!.trailingDistancePct! | percent: '1.0-1' }}</span> }
          @if (rr.rules?.maxOpenPositions != null) { <span class="risk-chip">Max positions {{ rr.rules!.maxOpenPositions }}</span> }
          @if (rr.rules?.positionFraction != null) { <span class="risk-chip">Position size {{ rr.rules!.positionFraction! | percent: '1.0-1' }}</span> }
          @if (rr.rules?.lockedCapitalPct != null) { <span class="risk-chip">Locked capital {{ rr.rules!.lockedCapitalPct! | percent: '1.0-0' }}</span> }
          @if (rr.rules?.minHoldDays != null) { <span class="risk-chip">Probation day {{ rr.rules!.minHoldDays }}</span> }
          @if (rr.rules?.momentumHealthThreshold != null) { <span class="risk-chip">Health floor {{ rr.rules!.momentumHealthThreshold }}</span> }
          @if (rr.rules?.excludedSetups?.length) { <span class="risk-chip">Excluded: {{ rr.rules!.excludedSetups!.join(', ') }}</span> }
        </div>
        @if (rr.rules?.setupTactics?.length) {
          <div class="tactics-list">
            @for (t of rr.rules!.setupTactics!; track t.setup) {
              <span>{{ t.setup }}: stop {{ t.stopLossPct | percent: '1.0-1' }} · target {{ t.targetPct | percent: '1.0-1' }} · hold {{ t.guideHoldDays }}d</span>
            }
          </div>
        }
        <p class="shadow-note">
          Re-applying below covers the weights only — risk settings were written to the {{ rr.targetRegime }} book at
          apply time and are shown here for the audit trail.
        </p>
      }

      @if (s.isShadowMode) {
        <p class="shadow-note">Shadow mode — enable Refinement:Active before these can be applied.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Close</button>
      <button mat-raised-button color="primary" [disabled]="s.isShadowMode || applying()" (click)="apply()">
        Apply weights to live settings
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .chips { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; }
    .confidence { font-weight: 600; }
    .confidence.low { color: var(--st-red); }
    .confidence.medium { color: var(--st-amber); }
    .confidence.high { color: var(--st-green); }
    .origin-chip, .status-chip {
      font-size: 11px; text-transform: uppercase; letter-spacing: 0.03em;
      border-radius: 10px; padding: 2px 8px; background: rgba(128,128,128,0.15); color: var(--st-muted);
    }
    .origin-chip.lab { background: rgba(99,102,241,0.18); color: #818cf8; }
    .period { color: var(--st-muted); font-size: 13px; }
    .stat-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 6px 24px; margin: 10px 0; }
    .stat-grid .k { color: var(--st-muted); font-size: 12px; margin-right: 8px; }
    .stat-grid .v { font-weight: 600; }
    .warning { color: var(--st-amber); }
    .replay { font-size: 13px; }
    .replay.pass { color: var(--st-green, #2e9b57); }
    .replay.fail { color: var(--st-amber); }
    .assessment { font-style: italic; }
    .shadow-note { color: var(--st-muted); font-size: 12px; }
    .risk-chips { display: flex; flex-wrap: wrap; gap: 6px; }
    .risk-chip { font-size: 12px; border-radius: 10px; padding: 2px 10px; background: rgba(128,128,128,0.15); }
    .tactics-list { display: flex; flex-direction: column; gap: 2px; margin-top: 6px; font-size: 12px; color: var(--st-muted); }
  `],
})
export class RefinementDetailDialogComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);
  private ref = inject(MatDialogRef<RefinementDetailDialogComponent, boolean>);

  s = inject<RefinementSuggestionDto>(MAT_DIALOG_DATA);
  applying = signal(false);

  apply(): void {
    const confirm = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '440px',
      data: {
        title: 'Apply these weights to live settings?',
        message:
          `This replaces your active strategy weights with the ones from this ` +
          `refinement run (generated ${new Date(this.s.generatedAt).toLocaleDateString()}). ` +
          `It takes effect on the next research run.`,
        cancelLabel: 'Cancel',
        confirmLabel: 'Apply to live',
        confirmColor: 'warn',
      },
    });

    confirm.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.applying.set(true);
      this.api.applyRefinement(this.s.id).subscribe({
        next: (r) => {
          this.snackbar.open(r.message ?? 'Weights applied to live settings', 'Dismiss', { duration: 4000 });
          this.ref.close(true);
        },
        error: (err) => {
          this.applying.set(false);
          this.snackbar.open(errorMessage(err, 'Failed to apply weights.'), 'Dismiss', { duration: 4000 });
        },
      });
    });
  }
}
