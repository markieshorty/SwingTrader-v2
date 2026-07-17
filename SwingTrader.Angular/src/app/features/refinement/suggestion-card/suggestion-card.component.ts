import { CommonModule } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { FormsModule } from '@angular/forms';
import { WeightEditorComponent } from '../weight-editor/weight-editor.component';
import { RefinementSuggestionDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-suggestion-card',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule, WeightEditorComponent],
  template: `
    <div class="suggestion">
      <div class="header">
        <span class="confidence" [class]="suggestion().confidenceLevel.toLowerCase()">
          {{ suggestion().confidenceLevel }} confidence
        </span>
        <span class="origin-chip" [class.lab]="suggestion().origin === 'StrategyLab'" [class.share]="suggestion().origin === 'SharedStrategy'">
          {{ suggestion().origin === 'StrategyLab' ? 'Strategy Lab' : suggestion().origin === 'SharedStrategy' ? 'Shared strategy' : 'Auto refinement' }}
        </span>
        <span class="period">
          {{ suggestion().analysisPeriodStart | date: 'mediumDate' }} –
          {{ suggestion().analysisPeriodEnd | date: 'mediumDate' }}
        </span>
      </div>

      <p class="stats">
        {{ suggestion().tradeCountAnalysed }} trades analysed · {{ suggestion().winnerCount }} winners ·
        {{ suggestion().loserCount }} losers · {{ suggestion().overallWinRate | percent: '1.0-1' }} win rate
      </p>

      @if (suggestion().unusualMarketConditions && suggestion().marketConditionWarning) {
        <p class="warning">⚠️ {{ suggestion().marketConditionWarning }}</p>
      }

      @if (suggestion().replayCheckPassed !== null && suggestion().replayCheckPassed !== undefined) {
        @if (suggestion().replayCheckPassed) {
          <p class="replay-check pass">
            ✓ Replay check passed — on the same trade history, the suggested weights would have averaged
            {{ suggestion().replaySuggestedAvgReturnPct | number: '1.2-2' }}%/trade vs
            {{ suggestion().replayCurrentAvgReturnPct | number: '1.2-2' }}% under current weights
            ({{ suggestion().replayTradesKept }} trades kept).
          </p>
        } @else {
          <p class="warning">
            ⚠️ Replay check FAILED — replaying your own history, these suggested weights would have averaged
            {{ suggestion().replaySuggestedAvgReturnPct | number: '1.2-2' }}%/trade vs
            {{ suggestion().replayCurrentAvgReturnPct | number: '1.2-2' }}% under current weights. The correlation
            points one way but the outcome points the other — applying is not recommended.
          </p>
        }
      }

      @if (suggestion().assessmentSummary) {
        <p class="assessment">{{ suggestion().assessmentSummary }}</p>
      }

      <app-weight-editor
        [currentWeights]="suggestion().currentWeights"
        [suggestedWeights]="suggestion().suggestedWeights"
      />

      @if (suggestion().suggestedRiskRules; as rr) {
        <div class="risk-rules">
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
            <div class="tactics-list muted small">
              @for (t of rr.rules!.setupTactics!; track t.setup) {
                <span>{{ t.setup }}: stop {{ t.stopLossPct | percent: '1.0-1' }} · target {{ t.targetPct | percent: '1.0-1' }} · hold {{ t.guideHoldDays }}d</span>
              }
            </div>
          }
          <p class="muted-note">These landed on the {{ rr.targetRegime }} risk book / setup tactics when this suggestion was applied.</p>
        </div>
      }

      @if (suggestion().isShadowMode) {
        <p class="shadow-note">Shadow mode — enable Refinement:Active to apply.</p>
        <button mat-raised-button disabled>Apply General Weights</button>
      } @else if (suggestion().status === 'Pending') {
        <div class="actions">
          <button mat-raised-button color="primary" (click)="apply.emit()">Apply General Weights</button>
          <mat-form-field appearance="outline" class="note-field">
            <mat-label>Rejection note (optional)</mat-label>
            <input matInput [(ngModel)]="rejectNote" />
          </mat-form-field>
          <button mat-stroked-button color="warn" (click)="reject.emit(rejectNote)">Reject</button>
        </div>
      } @else {
        <div class="actions">
          <button mat-raised-button color="primary" (click)="apply.emit()">Re-apply to live</button>
          <span class="muted-note">
            Status: {{ suggestion().status }}. Re-applying creates a fresh active weights row from this suggestion
            (weights only — risk settings shown above are not re-applied).
          </span>
        </div>
      }
    </div>
  `,
  styles: [
    `
      .header {
        display: flex;
        justify-content: space-between;
        margin-bottom: 8px;
        font-size: 13px;
      }
      .confidence {
        font-weight: 600;
      }
      .confidence.low {
        color: var(--st-red);
      }
      .confidence.medium {
        color: var(--st-amber);
      }
      .confidence.high {
        color: var(--st-green);
      }
      .period {
        color: var(--st-muted);
      }
      .origin-chip {
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 0.03em;
        border-radius: 10px;
        padding: 2px 8px;
        background: rgba(128, 128, 128, 0.15);
        color: var(--st-muted);
      }
      .origin-chip.lab {
        background: rgba(99, 102, 241, 0.18);
        color: #818cf8;
      }
      .origin-chip.share {
        background: rgba(46, 155, 87, 0.18);
        color: #4ade80;
      }
      .stats {
        font-size: 13px;
        color: var(--st-muted);
      }
      .warning {
        color: var(--st-amber);
      }
      .replay-check.pass {
        color: var(--st-green, #2e9b57);
        font-size: 13px;
      }
      .assessment {
        font-style: italic;
        color: var(--st-text);
      }
      .shadow-note {
        color: var(--st-muted);
        font-size: 12px;
      }
      .actions {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-top: 12px;
      }
      .note-field {
        flex: 1;
      }
      .risk-rules {
        margin-top: 12px;
        padding-top: 10px;
        border-top: 1px solid rgba(128, 128, 128, 0.2);

        h4 {
          margin: 0 0 6px;
          font-size: 0.85rem;
        }
      }
      .risk-chips {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }
      .risk-chip {
        font-size: 12px;
        border-radius: 10px;
        padding: 2px 10px;
        background: rgba(128, 128, 128, 0.15);
      }
      .tactics-list {
        display: flex;
        flex-direction: column;
        gap: 2px;
        margin-top: 6px;
      }
      .muted-note {
        color: var(--st-muted);
        font-size: 12px;
        margin-top: 6px;
      }
    `,
  ],
})
export class SuggestionCardComponent {
  suggestion = input.required<RefinementSuggestionDto>();
  apply = output<void>();
  reject = output<string>();
  rejectNote = '';
}
