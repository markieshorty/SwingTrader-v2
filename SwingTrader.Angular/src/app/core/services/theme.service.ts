import { Injectable, signal } from '@angular/core';

// Light/dark theme switch. Dark is the default (and the app's original
// look); light mode is very pale blues with near-black text. The choice
// persists in localStorage and is applied as a body class so the global
// stylesheet's `body.light-theme` overrides (CSS vars + Material colors)
// take effect everywhere at once.
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private static readonly StorageKey = 'cadentic.theme';

  theme = signal<'dark' | 'light'>('dark');

  constructor() {
    const saved = localStorage.getItem(ThemeService.StorageKey);
    if (saved === 'light') this.apply('light');
  }

  toggle(): void {
    this.apply(this.theme() === 'dark' ? 'light' : 'dark');
  }

  private apply(theme: 'dark' | 'light'): void {
    this.theme.set(theme);
    document.body.classList.toggle('light-theme', theme === 'light');
    localStorage.setItem(ThemeService.StorageKey, theme);
  }
}
