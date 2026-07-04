import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full',
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
    title: 'Dashboard',
  },
  {
    path: 'signals',
    loadComponent: () =>
      import('./features/signals/signals.component').then((m) => m.SignalsComponent),
    title: 'Signals',
  },
  {
    path: 'trades',
    loadComponent: () =>
      import('./features/trades/trades.component').then((m) => m.TradesComponent),
    title: 'Trades',
  },
  {
    path: 'refinement',
    loadComponent: () =>
      import('./features/refinement/refinement.component').then((m) => m.RefinementComponent),
    title: 'Refinement',
  },
  {
    path: 'readiness',
    loadComponent: () =>
      import('./features/readiness/readiness.component').then((m) => m.ReadinessComponent),
    title: 'Readiness',
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./features/settings/settings.component').then((m) => m.SettingsComponent),
    title: 'Settings',
  },
  {
    path: '**',
    redirectTo: 'dashboard',
  },
];
