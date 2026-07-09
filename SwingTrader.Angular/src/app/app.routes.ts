import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { approvalGuard, approvalCompleteGuard } from './core/guards/approval.guard';
import { onboardingGuard, onboardingCompleteGuard } from './core/guards/onboarding.guard';
import { adminGuard } from './core/guards/admin.guard';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('./features/splash/splash.component').then((m) => m.SplashComponent),
    title: 'Acme Trading',
  },
  {
    path: 'join',
    loadComponent: () => import('./features/splash/splash.component').then((m) => m.SplashComponent),
    title: 'Join account',
  },
  {
    path: 'pending-approval',
    canActivate: [authGuard, approvalCompleteGuard],
    loadComponent: () =>
      import('./features/pending-approval/pending-approval.component').then((m) => m.PendingApprovalComponent),
    title: 'Awaiting approval',
  },
  {
    path: 'onboarding',
    canActivate: [authGuard, approvalGuard, onboardingCompleteGuard],
    loadComponent: () =>
      import('./features/onboarding/onboarding.component').then((m) => m.OnboardingComponent),
    title: 'Get started',
  },
  {
    path: 'dashboard',
    canActivate: [authGuard, approvalGuard, onboardingGuard],
    loadComponent: () =>
      import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
    title: 'Dashboard',
  },
  {
    path: 'signals',
    canActivate: [authGuard, approvalGuard, onboardingGuard],
    loadComponent: () =>
      import('./features/signals/signals.component').then((m) => m.SignalsComponent),
    title: 'Signals',
  },
  {
    path: 'trades',
    canActivate: [authGuard, approvalGuard, onboardingGuard],
    loadComponent: () =>
      import('./features/trades/trades.component').then((m) => m.TradesComponent),
    title: 'Trades',
  },
  {
    path: 'refinement',
    canActivate: [authGuard, approvalGuard, onboardingGuard],
    loadComponent: () =>
      import('./features/refinement/refinement.component').then((m) => m.RefinementComponent),
    title: 'Refinement',
  },
  {
    path: 'readiness',
    canActivate: [authGuard, approvalGuard, onboardingGuard],
    loadComponent: () =>
      import('./features/readiness/readiness.component').then((m) => m.ReadinessComponent),
    title: 'Readiness',
  },
  {
    path: 'watchlists',
    canActivate: [authGuard, approvalGuard, onboardingGuard],
    loadComponent: () =>
      import('./features/watchlists/watchlists.component').then((m) => m.WatchlistsComponent),
    title: 'Watchlists',
  },
  {
    path: 'admin',
    canActivate: [authGuard, adminGuard],
    loadComponent: () => import('./features/admin/admin.component').then((m) => m.AdminComponent),
    title: 'Admin',
  },
  {
    path: 'admin/users/:userId',
    canActivate: [authGuard, adminGuard],
    loadComponent: () =>
      import('./features/admin/admin-user-view/admin-user-view.component').then((m) => m.AdminUserViewComponent),
    title: 'Admin · User',
  },
  {
    path: 'settings',
    canActivate: [authGuard, approvalGuard],
    loadComponent: () =>
      import('./features/settings/settings.component').then((m) => m.SettingsComponent),
    title: 'Settings',
  },
  {
    path: 'guide',
    canActivate: [authGuard, approvalGuard],
    loadComponent: () => import('./features/guide/guide.component').then((m) => m.GuideComponent),
    title: 'Guide',
  },
  {
    path: '**',
    redirectTo: '',
  },
];
