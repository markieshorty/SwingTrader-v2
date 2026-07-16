import { CommonModule } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
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
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/services/api.service';
import { EconomicLinkDto, UniverseSymbolDto, WatchlistDto, WatchlistItemDto, WatchlistType } from '../../core/models/dtos';
// WatchlistTargetSizeDto used via api.service return type inference.
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
    MatSliderModule,
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
  // Deduplicated - what Research actually scans (a symbol on two enabled
  // watchlists is researched once). This is the number that matters for the
  // MAX_TOTAL_ENABLED_SYMBOLS cap.
  totalEnabledSymbolCount = () => {
    const symbols = new Set<string>();
    for (const w of this.watchlists()) {
      if (w.isEnabled) for (const item of w.items) symbols.add(item.symbol.toUpperCase());
    }
    return symbols.size;
  };
  // Raw sum of every enabled watchlist's item count, duplicates included -
  // shown alongside the deduplicated count so "89 vs 92 total items" isn't
  // mistaken for missing/hidden symbols (e.g. top movers) when it's really
  // just the same symbol counted twice across two enabled watchlists.
  rawEnabledSymbolCount = () =>
    this.watchlists()
      .filter((w) => w.isEnabled)
      .reduce((sum, w) => sum + w.items.length, 0);
  hasDuplicateEnabledSymbols = () => this.rawEnabledSymbolCount() !== this.totalEnabledSymbolCount();
  maxEnabled = MAX_ENABLED_WATCHLISTS;
  maxSymbols = MAX_SYMBOLS_PER_WATCHLIST;
  maxTotalSymbols = MAX_TOTAL_ENABLED_SYMBOLS;

  // Account-level target size for the weekly AI-managed watchlist refresh
  // (how many symbols Claude picks). Moved here from Risk Management - it's a
  // watchlist concern, not a per-regime risk setting.
  targetSize = signal<number | null>(null);
  private targetSizeOriginal: number | null = null;
  targetSizeRange = signal<{ min: number; max: number }>({ min: 10, max: 50 });
  targetSizeSaving = signal(false);
  targetSizeDirty = computed(() =>
    this.targetSize() !== null && this.targetSize() !== this.targetSizeOriginal);

  // Account-level size for the weekly QUALITATIVE watchlist refresh (how many
  // symbols Claude picks on narrative grounds). Separate, smaller knob than the
  // technical target size above; the qualitative list is created disabled, so
  // it doesn't consume the 100-symbol enabled cap until the user enables it -
  // no spare-capacity cap is needed here.
  qualitativeSize = signal<number | null>(null);
  private qualitativeSizeOriginal: number | null = null;
  qualitativeSizeRange = signal<{ min: number; max: number }>({ min: 5, max: 20 });
  qualitativeSizeSaving = signal(false);
  qualitativeSizeDirty = computed(() =>
    this.qualitativeSize() !== null && this.qualitativeSize() !== this.qualitativeSizeOriginal);

  // Spare capacity for the AI-managed (technical) list: the global 100-symbol
  // cap minus the symbols already held by every OTHER enabled watchlist. The
  // AI-managed list's own current picks are excluded because the weekly
  // refresh REPLACES them - what has to fit is `target` new picks alongside
  // everything else that's enabled. Worst case (no overlap) is target + others,
  // so the largest target that can never breach the cap is 100 - others.
  targetSizeSpare = computed(() => {
    const others = new Set<string>();
    for (const w of this.watchlists()) {
      if (w.isEnabled && w.type !== 'AiManaged') {
        for (const item of w.items) others.add(item.symbol.toUpperCase());
      }
    }
    return MAX_TOTAL_ENABLED_SYMBOLS - others.size;
  });
  // The slider's usable max: the configured range max, pulled down to spare
  // capacity, but never below the range min (mat-slider requires max >= min).
  targetSizeEffectiveMax = computed(() =>
    Math.max(this.targetSizeRange().min, Math.min(this.targetSizeRange().max, this.targetSizeSpare())));
  // Spare capacity is squeezing the slider below its natural max.
  targetSizeCapped = computed(() => this.targetSizeSpare() < this.targetSizeRange().max);
  // Even the smallest allowed target won't fit - the user must free up places
  // on other watchlists before the AI list can be sized at all.
  targetSizeOverCapacity = computed(() => this.targetSizeSpare() < this.targetSizeRange().min);

  // Second-hop economic links (docs/second-hop-plan): per-symbol viewer with
  // the Owner-only suppress toggle - the hallucinated-link kill switch.
  linksSymbol = signal<string | null>(null);
  links = signal<EconomicLinkDto[]>([]);
  linksLoading = signal(false);

  constructor() {
    this.load();
    this.api.getAccountSettings().subscribe({
      next: (s) => this.isOwner.set(s.role === 'Owner'),
      error: () => {},
    });
    this.api.getWatchlistTargetSize().subscribe({
      next: (t) => {
        this.targetSize.set(t.targetWatchlistSize);
        this.targetSizeOriginal = t.targetWatchlistSize;
        this.targetSizeRange.set({ min: t.min, max: t.max });
      },
      error: () => {},
    });
    this.api.getQualitativeWatchlistSize().subscribe({
      next: (t) => {
        this.qualitativeSize.set(t.qualitativeWatchlistSize);
        this.qualitativeSizeOriginal = t.qualitativeWatchlistSize;
        this.qualitativeSizeRange.set({ min: t.min, max: t.max });
      },
      error: () => {},
    });
    // ?links=SYMBOL deep-link (from the Intelligence second-hop tab): open
    // that symbol's economic-links panel directly.
    const linksParam = inject(ActivatedRoute).snapshot.queryParamMap.get('links');
    if (linksParam) this.toggleLinks(linksParam.toUpperCase());

    // Once both the saved target and the watchlists have loaded, pull a
    // stale-high target down to the spare-capacity ceiling so the slider can
    // never sit above what would actually fit under the 100-symbol cap. Only
    // ever tightens - it never nudges the value up on the user's behalf.
    effect(() => {
      const value = this.targetSize();
      const max = this.targetSizeEffectiveMax();
      if (value !== null && value > max) this.targetSize.set(max);
    }, { allowSignalWrites: true });
  }

  toggleLinks(symbol: string): void {
    if (this.linksSymbol() === symbol) {
      this.linksSymbol.set(null);
      return;
    }
    this.linksSymbol.set(symbol);
    this.linksLoading.set(true);
    this.links.set([]);
    this.api.getEconomicLinks(symbol).subscribe({
      next: (links) => {
        this.links.set(links);
        this.linksLoading.set(false);
      },
      error: () => this.linksLoading.set(false),
    });
  }

  setLinkSuppressed(link: EconomicLinkDto, suppressed: boolean): void {
    this.api.setEconomicLinkSuppressed(link.id, suppressed).subscribe({
      next: () => this.links.set(this.links().map((l) => (l.id === link.id ? { ...l, suppressed } : l))),
      error: (err) => this.snackbar.open(errorMessage(err, 'Failed to update the link.'), 'Dismiss', { duration: 4000 }),
    });
  }

  saveTargetSize(): void {
    const value = this.targetSize();
    if (value === null || !this.targetSizeDirty()) return;
    // Never persist a target above spare capacity, even if a stale value
    // slipped past the slider - the clamp effect normally handles this, but
    // guard the save path too.
    if (value > this.targetSizeEffectiveMax()) return;
    this.targetSizeSaving.set(true);
    this.api.updateWatchlistTargetSize(value).subscribe({
      next: () => {
        this.targetSizeOriginal = value;
        this.targetSizeSaving.set(false);
        this.snackbar.open(`Watchlist size set to ${value} symbols.`, 'Dismiss', { duration: 3000 });
      },
      error: (err) => {
        this.targetSizeSaving.set(false);
        this.snackbar.open(errorMessage(err, 'Failed to save the watchlist size.'), 'Dismiss', { duration: 4000 });
      },
    });
  }

  saveQualitativeSize(): void {
    const value = this.qualitativeSize();
    if (value === null || !this.qualitativeSizeDirty()) return;
    this.qualitativeSizeSaving.set(true);
    this.api.updateQualitativeWatchlistSize(value).subscribe({
      next: () => {
        this.qualitativeSizeOriginal = value;
        this.qualitativeSizeSaving.set(false);
        this.snackbar.open(`Qualitative watchlist size set to ${value} symbols.`, 'Dismiss', { duration: 3000 });
      },
      error: (err) => {
        this.qualitativeSizeSaving.set(false);
        this.snackbar.open(errorMessage(err, 'Failed to save the qualitative watchlist size.'), 'Dismiss', { duration: 4000 });
      },
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
    const opening = this.expandedId() !== id;
    this.expandedId.set(opening ? id : null);
    // AI-picked lists carry a "[Archetype] reason" per symbol in the history
    // - fetch it on first expand so the review-before-enable read isn't blind.
    const watchlist = this.watchlists().find((w) => w.id === id);
    if (opening && watchlist?.type === 'AiQualitative' && !this.rationales()[id]) {
      this.api.getWatchlistRationales(id).subscribe({
        next: (r) => this.rationales.set({ ...this.rationales(), [id]: r }),
        error: () => {},
      });
    }
  }

  // Per-watchlist-id map of symbol -> "[Archetype] reason".
  rationales = signal<Record<number, Record<string, string>>>({});

  rationaleFor(watchlistId: number, symbol: string): string | null {
    return this.rationales()[watchlistId]?.[symbol.toUpperCase()] ?? null;
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
    return { AiManaged: 'ai', Manual: 'manual', Mixed: 'mixed', AiQualitative: 'ai' }[type];
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
