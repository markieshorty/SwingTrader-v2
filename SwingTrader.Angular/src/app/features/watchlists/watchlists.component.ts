import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/services/api.service';
import { UniverseSymbolDto, WatchlistDto, WatchlistItemDto, WatchlistType } from '../../core/models/dtos';
import { errorMessage } from '../../shared/utils/error-message.util';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';

const MAX_ENABLED_WATCHLISTS = 10;
const MAX_SYMBOLS_PER_WATCHLIST = 50;
const MAX_TOTAL_ENABLED_SYMBOLS = 100;

@Component({
  selector: 'app-watchlists',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatChipsModule,
    MatTooltipModule,
    MatSlideToggleModule,
    MatDialogModule,
    MatTabsModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './watchlists.component.html',
  styleUrl: './watchlists.component.scss',
})
export class WatchlistsComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  private dialog = inject(MatDialog);

  watchlists = signal<WatchlistDto[]>([]);
  expandedId = signal<number | null>(null);
  loaded = signal(false);

  newSymbolInput: Record<number, string> = {};

  showCreateForm = signal(false);
  newName = '';
  newType: WatchlistType = 'Manual';
  newDescription = '';
  isOwner = signal(false);

  // Stock List Universe tab — the full screening pool, lazy-loaded on first view.
  universe = signal<UniverseSymbolDto[]>([]);
  universeLoading = signal(false);
  universeError = signal(false);
  universeQuery = signal('');
  filteredUniverse = computed(() => {
    const q = this.universeQuery().trim().toLowerCase();
    const all = this.universe();
    if (!q) return all;
    return all.filter((u) => u.symbol.toLowerCase().includes(q) || u.companyName.toLowerCase().includes(q));
  });

  enabledCount = () => this.watchlists().filter((w) => w.isEnabled).length;
  totalEnabledSymbolCount = () => {
    const symbols = new Set<string>();
    for (const w of this.watchlists()) {
      if (w.isEnabled) for (const item of w.items) symbols.add(item.symbol.toUpperCase());
    }
    return symbols.size;
  };
  maxEnabled = MAX_ENABLED_WATCHLISTS;
  maxSymbols = MAX_SYMBOLS_PER_WATCHLIST;
  maxTotalSymbols = MAX_TOTAL_ENABLED_SYMBOLS;

  constructor() {
    this.load();
    this.api.getAccountSettings().subscribe({
      next: (s) => this.isOwner.set(s.role === 'Owner'),
      error: () => {},
    });
  }

  private load(): void {
    this.api.getWatchlists().subscribe({
      next: (watchlists) => {
        this.watchlists.set(watchlists);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(true),
    });
  }

  toggleExpanded(id: number): void {
    this.expandedId.set(this.expandedId() === id ? null : id);
  }

  onTabChange(index: number): void {
    // Lazy-load the universe the first time the Stock List Universe tab opens.
    if (index === 1 && this.universe().length === 0 && !this.universeLoading()) {
      this.loadUniverse();
    }
  }

  loadUniverse(): void {
    this.universeLoading.set(true);
    this.universeError.set(false);
    this.api.getUniverse().subscribe({
      next: (u) => {
        this.universe.set(u);
        this.universeLoading.set(false);
      },
      error: () => {
        this.universeError.set(true);
        this.universeLoading.set(false);
      },
    });
  }

  toggleEnabled(watchlist: WatchlistDto): void {
    if (!watchlist.isEnabled) {
      const projectedCount = this.projectedEnabledUnionCount({ includeWatchlistId: watchlist.id });
      if (projectedCount > MAX_TOTAL_ENABLED_SYMBOLS) {
        this.snackbar.open(
          `Enabling "${watchlist.name}" would bring the total across all enabled watchlists to ${projectedCount} symbols, ` +
            `above the ${MAX_TOTAL_ENABLED_SYMBOLS} limit. Disable another watchlist first, or remove some symbols.`,
          'Dismiss',
          { duration: 6000 },
        );
        return;
      }
    }

    const action = watchlist.isEnabled ? this.api.disableWatchlist(watchlist.id) : this.api.enableWatchlist(watchlist.id);
    action.subscribe({
      next: () => this.load(),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to update watchlist.'), 'Dismiss', { duration: 4000 }),
    });
  }

  // Mirrors the backend's dedup-by-symbol union check (WatchlistRepository.
  // EnableWatchlistAsync/AddSymbolAsync) so the user gets an immediate answer
  // without a round-trip - the API still re-validates, since watchlist
  // contents could have changed in another tab/session since this page loaded.
  private projectedEnabledUnionCount(opts: { includeWatchlistId?: number; extraSymbol?: string }): number {
    const symbols = new Set<string>();
    for (const w of this.watchlists()) {
      if (w.isEnabled || w.id === opts.includeWatchlistId) {
        for (const item of w.items) symbols.add(item.symbol.toUpperCase());
      }
    }
    if (opts.extraSymbol) symbols.add(opts.extraSymbol.toUpperCase());
    return symbols.size;
  }

  setDefault(watchlist: WatchlistDto): void {
    this.api.setDefaultWatchlist(watchlist.id).subscribe({
      next: () => {
        this.snackbar.open(`"${watchlist.name}" is now the default watchlist`, 'Dismiss', { duration: 3000 });
        this.load();
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to set default.'), 'Dismiss', { duration: 4000 }),
    });
  }

  deleteWatchlist(watchlist: WatchlistDto): void {
    if (watchlist.isDefault) {
      this.snackbar.open('The default watchlist can\'t be deleted - set another one as default first.', 'Dismiss', {
        duration: 5000,
      });
      return;
    }
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Delete watchlist',
          message: `Delete "${watchlist.name}"? This removes all its symbols.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Delete',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.deleteWatchlist(watchlist.id).subscribe({
          next: () => this.load(),
          error: (err) => this.snackbar.open(errorMessage(err, 'Failed to delete.'), 'Dismiss', { duration: 4000 }),
        });
      });
  }

  addSymbol(watchlist: WatchlistDto): void {
    const symbol = (this.newSymbolInput[watchlist.id] ?? '').trim();
    if (!symbol) return;

    if (watchlist.isEnabled) {
      const alreadyInUnion = watchlist.items.some((i) => i.symbol.toUpperCase() === symbol.toUpperCase());
      if (!alreadyInUnion) {
        const projectedCount = this.projectedEnabledUnionCount({ extraSymbol: symbol });
        if (projectedCount > MAX_TOTAL_ENABLED_SYMBOLS) {
          this.snackbar.open(
            `Adding "${symbol.toUpperCase()}" would bring the total across all enabled watchlists to ${projectedCount} symbols, ` +
              `above the ${MAX_TOTAL_ENABLED_SYMBOLS} limit. Remove a symbol from another enabled watchlist, or disable one, first.`,
            'Dismiss',
            { duration: 6000 },
          );
          return;
        }
      }
    }

    this.api.addWatchlistSymbol(watchlist.id, symbol).subscribe({
      next: () => {
        this.newSymbolInput[watchlist.id] = '';
        this.load();
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to add symbol.'), 'Dismiss', { duration: 4000 }),
    });
  }

  removeSymbol(watchlist: WatchlistDto, symbol: string): void {
    this.api.removeWatchlistSymbol(watchlist.id, symbol).subscribe({
      next: () => this.load(),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to remove symbol.'), 'Dismiss', { duration: 4000 }),
    });
  }

  createWatchlist(): void {
    if (!this.newName.trim()) return;

    this.api.createWatchlist(this.newName.trim(), this.newType, this.newDescription.trim() || undefined).subscribe({
      next: () => {
        this.newName = '';
        this.newType = 'Manual';
        this.newDescription = '';
        this.showCreateForm.set(false);
        this.load();
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to create watchlist.'), 'Dismiss', { duration: 4000 }),
    });
  }

  typeBadgeClass(type: WatchlistType): string {
    return { AiManaged: 'ai', Manual: 'manual', Mixed: 'mixed' }[type];
  }

  toggleTopMovers(watchlist: WatchlistDto): void {
    this.api.updateWatchlist(watchlist.id, watchlist.name, watchlist.description ?? undefined, !watchlist.topMoversEnabled).subscribe({
      next: () => this.load(),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to update top movers setting.'), 'Dismiss', { duration: 4000 }),
    });
  }

  toggleForceIntoFinalList(watchlist: WatchlistDto, item: WatchlistItemDto, checked: boolean): void {
    this.api.setForceIntoFinalList(watchlist.id, item.symbol, checked).subscribe({
      next: () => {
        item.forceIntoFinalList = checked; // optimistic - avoids a full reload for a single flag
        this.snackbar.open(
          checked
            ? `${item.symbol} will be researched on the next trading day, regardless of this watchlist's enabled state.`
            : `${item.symbol} no longer forced into the final list.`,
          'Dismiss', { duration: 4000 },
        );
      },
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to update.'), 'Dismiss', { duration: 4000 }),
    });
  }
}
