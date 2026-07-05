import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { approvalGuard, approvalCompleteGuard } from './core/guards/approval.guard';
import { onboardingGuard, onboardingCompleteGuard } from './core/guards/onboarding.guard';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full',
  },
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login.component').then((m) => m.LoginComponent),
    title: 'Sign in',
  },
  {
    path: 'join',
    loadComponent: () => import('./features/auth/join.component').then((m) => m.JoinComponent),
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
    path: 'settings',
    canActivate: [authGuard, approvalGuard],
    loadComponent: () =>
      import('./features/settings/settings.component').then((m) => m.SettingsComponent),
    title: 'Settings',
  },
  {
    path: '**',
    redirectTo: 'dashboard',
  },
];
