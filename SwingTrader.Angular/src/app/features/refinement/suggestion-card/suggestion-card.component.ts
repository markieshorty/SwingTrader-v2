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
        <span class="period">
          {{ suggestion().analysisPeriodStart | date: 'mediumDate' }} –
          {{ suggestion().analysisPeriodEnd | date: 'mediumDate' }}
        </span>
      </div>

      <p class="stats">
        {{ suggestion().tradeCountAnalysed }} trades analysed · {{ suggestion().winnerCount }} winners ·
        {{ suggestion().loserCount }} losers · {{ suggestion().overallWinRate | number: '1.0-1' }}% win rate
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
    `,
  ],
})
export class SuggestionCardComponent {
  suggestion = input.required<RefinementSuggestionDto>();
  apply = output<void>();
  reject = output<string>();
  rejectNote = '';
}
