import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

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
    path: 'dashboard',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
    title: 'Dashboard',
  },
  {
    path: 'signals',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/signals/signals.component').then((m) => m.SignalsComponent),
    title: 'Signals',
  },
  {
    path: 'trades',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/trades/trades.component').then((m) => m.TradesComponent),
    title: 'Trades',
  },
  {
    path: 'refinement',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/refinement/refinement.component').then((m) => m.RefinementComponent),
    title: 'Refinement',
  },
  {
    path: 'readiness',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/readiness/readiness.component').then((m) => m.ReadinessComponent),
    title: 'Readiness',
  },
  {
    path: 'settings',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/settings/settings.component').then((m) => m.SettingsComponent),
    title: 'Settings',
  },
  {
    path: '**',
    redirectTo: 'dashboard',
  },
];
