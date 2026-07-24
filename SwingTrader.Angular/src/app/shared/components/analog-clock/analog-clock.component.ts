import {
  AfterViewInit, Component, ElementRef, NgZone, OnDestroy, ViewChild, input, signal,
} from '@angular/core';

// A labelled timezone clock for the sidebar: digital HH:mm:ss with an analog
// canvas face (hour/minute/second hands) underneath, ticking once a second.
// The canvas is drawn outside Angular's zone so the per-second repaint never
// triggers change detection; only the digital text goes through a signal.
@Component({
  selector: 'app-analog-clock',
  standalone: true,
  template: `
    <div class="clock">
      <span class="tz-label">{{ label() }}</span>
      <span class="digital">{{ digital() }}</span>
      <canvas #face width="72" height="72"></canvas>
    </div>
  `,
  styles: [
    `
      .clock {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 2px;
      }
      .tz-label {
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.06em;
        color: var(--st-muted);
      }
      .digital {
        font-size: 13px;
        font-weight: 600;
        font-variant-numeric: tabular-nums;
        color: var(--st-text);
      }
      canvas {
        margin-top: 2px;
      }
    `,
  ],
})
export class AnalogClockComponent implements AfterViewInit, OnDestroy {
  label = input.required<string>();
  timeZone = input.required<string>();

  digital = signal('');

  @ViewChild('face') private face!: ElementRef<HTMLCanvasElement>;
  private timer?: ReturnType<typeof setInterval>;

  constructor(private zone: NgZone) {}

  ngAfterViewInit(): void {
    this.tick();
    this.zone.runOutsideAngular(() => {
      this.timer = setInterval(() => this.tick(), 1000);
    });
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  private tick(): void {
    const now = new Date();
    const parts = new Intl.DateTimeFormat('en-GB', {
      timeZone: this.timeZone(),
      hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
    }).formatToParts(now);
    const get = (t: string) => Number(parts.find((p) => p.type === t)?.value ?? 0);
    const h = get('hour'), m = get('minute'), s = get('second');

    // Digital text is a signal so Angular renders it; run inside the zone.
    this.zone.run(() => this.digital.set(
      `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`));

    this.draw(h, m, s);
  }

  private draw(h: number, m: number, s: number): void {
    const canvas = this.face?.nativeElement;
    const ctx = canvas?.getContext('2d');
    if (!ctx) return;

    const css = getComputedStyle(document.body);
    const border = css.getPropertyValue('--st-border').trim() || '#334155';
    const text = css.getPropertyValue('--st-text').trim() || '#f1f5f9';
    const muted = css.getPropertyValue('--st-muted').trim() || '#94a3b8';
    const accent = css.getPropertyValue('--st-red').trim() || '#ef4444';

    const w = canvas.width, r = w / 2;
    ctx.clearRect(0, 0, w, w);
    ctx.save();
    ctx.translate(r, r);

    // Face + hour ticks
    ctx.beginPath();
    ctx.arc(0, 0, r - 2, 0, Math.PI * 2);
    ctx.strokeStyle = border;
    ctx.lineWidth = 2;
    ctx.stroke();
    for (let i = 0; i < 12; i++) {
      const a = (i / 12) * Math.PI * 2;
      ctx.beginPath();
      ctx.moveTo(Math.sin(a) * (r - 6), -Math.cos(a) * (r - 6));
      ctx.lineTo(Math.sin(a) * (r - 9), -Math.cos(a) * (r - 9));
      ctx.strokeStyle = muted;
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    const hand = (angle: number, length: number, width: number, color: string) => {
      ctx.beginPath();
      ctx.moveTo(0, 0);
      ctx.lineTo(Math.sin(angle) * length, -Math.cos(angle) * length);
      ctx.strokeStyle = color;
      ctx.lineWidth = width;
      ctx.lineCap = 'round';
      ctx.stroke();
    };

    hand(((h % 12) + m / 60) / 12 * Math.PI * 2, r * 0.45, 2.5, text);
    hand((m + s / 60) / 60 * Math.PI * 2, r * 0.65, 1.8, text);
    hand(s / 60 * Math.PI * 2, r * 0.75, 1, accent);

    // Centre pin
    ctx.beginPath();
    ctx.arc(0, 0, 1.8, 0, Math.PI * 2);
    ctx.fillStyle = accent;
    ctx.fill();

    ctx.restore();
  }
}
