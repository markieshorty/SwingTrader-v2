import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, interval, map, startWith } from 'rxjs';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RegimeBadgeComponent } from './shared/components/regime-badge/regime-badge.component';
import { ErrorCardComponent } from './shared/components/error-card/error-card.component';
import { RelativeTimePipe } from './shared/pipes/relative-time.pipe';
import { DashboardDataService } from './core/services/dashboard-data.service';
import { ApiService } from './core/services/api.service';
import { ActiveJobDto } from './core/models/dtos';
import { AuthService } from './core/services/auth.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatSidenavModule,
    MatToolbarModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
    MatDividerModule,
    MatProgressSpinnerModule,
    DatePipe,
    RegimeBadgeComponent,
    ErrorCardComponent,
    RelativeTimePipe,
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  data = inject(DashboardDataService);
  auth = inject(AuthService);
  private router = inject(Router);

  regime = toSignal(this.data.regime$, { initialValue: null });
  lastUpdated = this.data.lastUpdated;

  private currentTitle = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(() => this.titleFromUrl()),
      startWith(this.titleFromUrl()),
    ),
    { initialValue: 'Dashboard' },
  );

  pageTitle = computed(() => this.currentTitle());

  // Login/join pages render standalone (no sidenav chrome) since there's
  // no account context / nav data to show yet at that point.
  private currentUrl = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  isAuthRoute = computed(() => {
    const url = this.currentUrl();
    return (
      url === '/' ||
      url.startsWith('/join') ||
      url.startsWith('/onboarding') ||
      url.startsWith('/pending-approval')
    );
  });

  private titleFromUrl(): string {
    const path = this.router.url.split('?')[0];
    const segment = path.split('/')[1] ?? 'dashboard';
    if (!segment) return 'Dashboard';
    if (segment === 'shared-strategies') return 'Shared Strategies';
    return segment.charAt(0).toUpperCase() + segment.slice(1);
  }

  // Live clocks under the nav menu - Eastern is the market's own timezone,
  // shown alongside the visitor's local time so it's obvious at a glance
  // whether the market is open without doing the timezone math by hand.
  private now = signal(new Date());

  // Toolbar job indicator: in-flight long-running jobs (optimizer,
  // backtests, research, watchlist...) polled so progress survives
  // navigating away from the page that started them. 15s cadence while
  // something is running, 45s when idle; recently-finished jobs stay
  // visible for the API's 10-minute window with a check/cross.
  activeJobs = signal<ActiveJobDto[]>([]);
  runningJobs = computed(() => this.activeJobs().filter((j) => j.status === 'Queued' || j.status === 'Running'));
  finishedJobs = computed(() => this.activeJobs()
    .filter((j) => (j.status === 'Completed' || j.status === 'Failed') && !this.dismissedJobs().has(this.jobKey(j))));

  // Click-to-dismiss for finished chips. Keys persist in localStorage so a
  // poll refresh (or page reload) inside the API's 10-minute window doesn't
  // resurrect a dismissed chip; entries older than an hour are pruned on
  // load so the store never grows.
  private dismissedJobs = signal<Set<string>>(AppComponent.loadDismissedJobs());

  private jobKey(j: ActiveJobDto): string {
    return `${j.kind}:${j.label}:${j.completedAt ?? ''}`;
  }

  dismissJob(j: ActiveJobDto): void {
    const next = new Set(this.dismissedJobs());
    next.add(this.jobKey(j));
    this.dismissedJobs.set(next);
    const store: Record<string, number> = {};
    for (const k of next) store[k] = Date.now();
    localStorage.setItem('toolbar.dismissedJobs', JSON.stringify(store));
  }

  private static loadDismissedJobs(): Set<string> {
    try {
      const raw = JSON.parse(localStorage.getItem('toolbar.dismissedJobs') ?? '{}') as Record<string, number>;
      const cutoff = Date.now() - 3_600_000;
      return new Set(Object.entries(raw).filter(([, at]) => at >= cutoff).map(([k]) => k));
    } catch {
      return new Set();
    }
  }

  jobLabel(j: ActiveJobDto): string {
    if ((j.status === 'Queued' || j.status === 'Running') && j.progressTotal && j.progressTotal > 0)
      return `${j.label} ${j.progressCompleted ?? 0}/${j.progressTotal}`;
    if (j.status === 'Completed') return `${j.label} done`;
    if (j.status === 'Failed') return `${j.label} failed`;
    return j.status === 'Queued' ? `${j.label} queued` : `${j.label} running`;
  }

  private pollActiveJobs(): void {
    if (this.isAuthRoute()) return;
    this.api.getActiveJobs().subscribe({
      next: (r) => this.activeJobs.set(r.jobs),
      error: () => {},
    });
  }

  // Shared-strategy nav state: the item only renders once the account has
  // ever received a share (total > 0), with a badge for undecided ones.
  // Refreshed on navigation - a cheap count endpoint, no payloads.
  private api = inject(ApiService);
  shareCounts = signal<{ count: number; total: number }>({ count: 0, total: 0 });

  constructor() {
    interval(1000)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.now.set(new Date()));

    this.router.events
      .pipe(
        filter((e): e is NavigationEnd => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        if (this.isAuthRoute()) return;
        this.api.getStrategyShareCounts().subscribe({
          next: (c) => this.shareCounts.set(c),
          error: () => {},
        });
        this.pollActiveJobs();
      });

    // Adaptive poll: quick while a job runs (progress bar feel), lazy idle.
    let jobTick = 0;
    interval(15_000)
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        jobTick++;
        if (this.runningJobs().length > 0 || jobTick % 3 === 0) this.pollActiveJobs();
      });
  }

  private static readonly timeFormat: Intl.DateTimeFormatOptions = {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  };

  localTime = computed(() => this.now().toLocaleTimeString([], AppComponent.timeFormat));
  easternTime = computed(() =>
    this.now().toLocaleTimeString([], { ...AppComponent.timeFormat, timeZone: 'America/New_York' }),
  );
}
