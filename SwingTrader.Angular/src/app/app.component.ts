import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, interval, map, startWith } from 'rxjs';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { RegimeBadgeComponent } from './shared/components/regime-badge/regime-badge.component';
import { ErrorCardComponent } from './shared/components/error-card/error-card.component';
import { RelativeTimePipe } from './shared/pipes/relative-time.pipe';
import { DashboardDataService } from './core/services/dashboard-data.service';
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
    const segment = this.router.url.split('/')[1] ?? 'dashboard';
    if (!segment) return 'Dashboard';
    return segment.charAt(0).toUpperCase() + segment.slice(1);
  }

  // Live clocks under the nav menu - Eastern is the market's own timezone,
  // shown alongside the visitor's local time so it's obvious at a glance
  // whether the market is open without doing the timezone math by hand.
  private now = signal(new Date());

  constructor() {
    interval(1000)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.now.set(new Date()));
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
