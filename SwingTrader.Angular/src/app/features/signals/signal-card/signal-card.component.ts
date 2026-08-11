import { CommonModule } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { SignalDto } from '../../../core/models/dtos';
import { ConvictionBarComponent } from '../../../shared/components/conviction-bar/conviction-bar.component';

@Component({
  selector: 'app-signal-card',
  standalone: true,
  imports: [CommonModule, MatIconModule, ConvictionBarComponent],
  template: `
    <div class="signal-row" (click)="expanded.set(!expanded())">
      <span class="symbol">{{ signal().symbol }}</span>
      <span class="company">{{ signal().companyName }}</span>
      <span class="date">{{ signal().signalDate | date: 'dd/MM/yyyy' }}</span>
      <app-conviction-bar [signal]="signal()" />
      <span class="fwd" [class.pass]="!signal().wouldBeVetoed" [class.fail]="signal().wouldBeVetoed"
            [title]="forwardTitle()">
        Fwd {{ forwardLabel() }}
      </span>
      <span class="badge" [class]="signal().recommendation.toLowerCase()">{{
        signal().recommendation
      }}</span>
      <mat-icon>{{ expanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
    </div>
    @if (expanded()) {
      <div class="detail">
        <p>{{ signal().fundamentalNarrative ?? 'No fundamental narrative available.' }}</p>
        <div class="scores">
          <span>RSI {{ signal().rsiScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>MACD {{ signal().macdScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Volume {{ signal().volumeScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Sentiment {{ signal().sentimentComponentScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Setup {{ signal().setupQualityScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>RelStrength {{ signal().relativeStrengthScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>PriceLevel {{ signal().priceLevelScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Fundamental {{ signal().fundamentalMomentumScore?.toFixed(2) ?? 'n/a' }}</span>
        </div>
        @if (signal().gateScore !== null) {
          <div class="scores funnel"
            title="The FORWARD score decides Buys: a gate-passing signal below the account's forward floor is demoted to Watch. The gate is a quality filter (pass/fail), not a ranker - its bands have not predicted outcomes.">
            <span title="Combined buy-priority score (gate + forward, out of 20) - execution buys highest-combined first when slots are scarce.">
              <strong>{{ combinedScore().toFixed(1) }}</strong>/20
            </span>
            <span><strong>Forward {{ forwardLabel() }}</strong>{{ signal().forwardScoreDegraded ? ' (degraded)' : '' }}</span>
            <span>Gate {{ signal().gateScore?.toFixed(1) }}{{ signal().wouldPassGate ? ' ✓' : '' }}</span>
            @if (sizeMultiplier() !== null) {
              <span class="size-mult" [class.up]="sizeMultiplier()! > 1.005" [class.down]="sizeMultiplier()! < 0.995"
                title="Prospective F2 size multiplier from the forward score and the active book's aggressiveness — execution recomputes this at buy time, then applies the cash/slot clamps.">
                Size {{ sizeMultiplier()!.toFixed(2) }}×
              </span>
            }
            @if (signal().wouldBeVetoed) { <span class="veto">would veto</span> }
          </div>
        }
      </div>
    }
  `,
  styles: [
    `
      .signal-row {
        display: grid;
        grid-template-columns: 80px 1fr 90px 160px 76px 90px 24px;
        align-items: center;
        gap: 12px;
        padding: 10px 12px;
        border-bottom: 1px solid var(--st-border);
        cursor: pointer;
      }

      .fwd {
        font-size: 11.5px;
        font-weight: 700;
        text-align: center;
        padding: 2px 6px;
        border-radius: 999px;
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
        background: rgba(127, 127, 127, 0.12);
        color: var(--st-muted);
      }
      .fwd.pass { background: rgba(34, 197, 94, 0.15); color: var(--st-green); }
      .fwd.fail { background: rgba(239, 68, 68, 0.13); color: var(--st-red); }

      .size-mult {
        font-variant-numeric: tabular-nums;
        border-radius: 10px;
        padding: 0 6px;
        background: rgba(127, 127, 127, 0.12);
      }
      .size-mult.up { color: var(--st-green); }
      .size-mult.down { color: var(--st-red); }
      .symbol {
        font-weight: 600;
      }
      .company {
        color: var(--st-muted);
        font-size: 13px;
      }
      .date {
        color: var(--st-muted);
        font-size: 12px;
        font-variant-numeric: tabular-nums;
      }
      .badge {
        font-size: 11px;
        padding: 2px 8px;
        border-radius: 999px;
        text-align: center;
      }
      .badge.buy {
        background: #14532d;
        color: var(--st-green);
      }
      .badge.watch {
        background: #713f12;
        color: var(--st-amber);
      }
      .badge.hold {
        background: #1e3a8a;
        color: var(--st-blue);
      }
      .badge.avoid,
      .badge.sell {
        background: #7f1d1d;
        color: var(--st-red);
      }
      .detail {
        padding: 12px;
        background: var(--st-card);
        border-bottom: 1px solid var(--st-border);
        font-size: 12px;
      }
      .scores {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        color: var(--st-muted);
        margin-top: 8px;
      }
      .scores.funnel {
        border-top: 1px dashed var(--st-border);
        padding-top: 6px;
        font-style: italic;
      }
      .veto {
        color: var(--st-red);
      }
    `,
  ],
})
export class SignalCardComponent {
  signal = input.required<SignalDto>();

  // The active book's F2 dials (today's board only; null on historic rows).
  sizing = input<{ funnelMode: boolean; aggressiveness: number; maxTilt: number } | null>(null);

  // Combined buy-priority score (gate + forward, 0-20) - mirrors the
  // ExecutionService ordering. Missing/degraded forward = neutral 5.
  combinedScore = computed<number>(() => {
    const s = this.signal();
    const forward = s.forwardScoreDegraded || s.forwardScore === null ? 5 : s.forwardScore;
    return (s.convictionScore ?? 0) + forward;
  });

  // Mirror of PositionSizingService.ComputeForwardMultiplier - a preview of
  // what execution would apply, before the cash/slot clamps.
  sizeMultiplier = computed<number | null>(() => {
    const dials = this.sizing();
    const s = this.signal();
    if (!dials?.funnelMode || dials.aggressiveness <= 0) return null;
    if (s.forwardScore === null || s.forwardScoreDegraded) return null;
    const tilt = Math.max(-1, Math.min(1, (s.forwardScore - 5) / 5));
    return 1 + dials.aggressiveness * dials.maxTilt * tilt;
  });
  // "—" when stage 2 never ran (skipped for a sub-Watch gate), "n/a" when it
  // ran but degraded. Both are now VETOED rather than waved through: the
  // forward score is the only selector, so an unscored signal is the last
  // thing that should be bought.
  forwardLabel = computed<string>(() => {
    const s = this.signal();
    if (s.forwardScoreDegraded) return 'n/a';
    return s.forwardScore === null ? '—' : s.forwardScore.toFixed(1);
  });

  forwardTitle = computed<string>(() => {
    const s = this.signal();
    if (s.wouldBeVetoed) {
      return s.forwardScore === null || s.forwardScoreDegraded
        ? 'No usable forward score, so this cannot be bought — the forward score is the only selector, and an unscored signal is vetoed rather than waved through.'
        : `Forward ${s.forwardScore.toFixed(1)} is below the account's forward floor — demoted to Watch.`;
    }
    return `Forward ${this.forwardLabel()} clears the account's forward floor. This is the score that decides Buys.`;
  });

  expanded = signal(false);
}
