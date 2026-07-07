import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/services/api.service';
import { WatchlistDto, WatchlistType } from '../../core/models/dtos';
import { errorMessage } from '../../shared/utils/error-message.util';

const MAX_ENABLED_WATCHLISTS = 3;
const MAX_SYMBOLS_PER_WATCHLIST = 50;

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
  ],
  templateUrl: './watchlists.component.html',
  styleUrl: './watchlists.component.scss',
})
export class WatchlistsComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);

  watchlists = signal<WatchlistDto[]>([]);
  expandedId = signal<number | null>(null);
  loaded = signal(false);

  newSymbolInput: Record<number, string> = {};

  showCreateForm = signal(false);
  newName = '';
  newType: WatchlistType = 'Manual';
  newDescription = '';
  isOwner = signal(false);

  enabledCount = () => this.watchlists().filter((w) => w.isEnabled).length;
  maxEnabled = MAX_ENABLED_WATCHLISTS;
  maxSymbols = MAX_SYMBOLS_PER_WATCHLIST;

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

  toggleEnabled(watchlist: WatchlistDto): void {
    const action = watchlist.isEnabled ? this.api.disableWatchlist(watchlist.id) : this.api.enableWatchlist(watchlist.id);
    action.subscribe({
      next: () => this.load(),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to update watchlist.'), 'Dismiss', { duration: 4000 }),
    });
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
    if (!confirm(`Delete "${watchlist.name}"? This removes all its symbols.`)) return;

    this.api.deleteWatchlist(watchlist.id).subscribe({
      next: () => this.load(),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to delete.'), 'Dismiss', { duration: 4000 }),
    });
  }

  addSymbol(watchlist: WatchlistDto): void {
    const symbol = (this.newSymbolInput[watchlist.id] ?? '').trim();
    if (!symbol) return;

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
}
