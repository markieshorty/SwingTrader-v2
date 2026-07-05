import { Injectable, OnDestroy, signal } from '@angular/core';
import {
  BehaviorSubject,
  Observable,
  Subject,
  catchError,
  forkJoin,
  interval,
  map,
  of,
  startWith,
  switchMap,
  takeUntil,
  tap,
} from 'rxjs';
import { ApiService } from './api.service';
import { PortfolioDto, PositionDto, RegimeDto, SignalGroupDto, StatusDto } from '../models/dtos';

@Injectable({ providedIn: 'root' })
export class DashboardDataService implements OnDestroy {
  private readonly pollIntervalMs = 60_000;
  private destroy$ = new Subject<void>();

  private portfolioSubject = new BehaviorSubject<PortfolioDto | null>(null);
  private positionsSubject = new BehaviorSubject<PositionDto[]>([]);
  private signalsSubject = new BehaviorSubject<SignalGroupDto | null>(null);
  private statusSubject = new BehaviorSubject<StatusDto | null>(null);
  private regimeSubject = new BehaviorSubject<RegimeDto | null>(null);

  portfolio$ = this.portfolioSubject.asObservable();
  positions$ = this.positionsSubject.asObservable();
  signals$ = this.signalsSubject.asObservable();
  status$ = this.statusSubject.asObservable();
  regime$ = this.regimeSubject.asObservable();

  lastUpdated = signal<Date | null>(null);
  isLoading = signal<boolean>(false);
  error = signal<string | null>(null);

  constructor(private api: ApiService) {
    this.startPolling();
  }

  private startPolling(): void {
    interval(this.pollIntervalMs)
      .pipe(
        startWith(0),
        switchMap(() => this.fetchAll()),
        takeUntil(this.destroy$),
      )
      .subscribe();
  }

  private fetchAll(): Observable<void> {
    this.isLoading.set(true);
    this.error.set(null);

    // Each call is caught individually - a 404 on /api/portfolio (no
    // snapshot yet, expected for a brand-new account before any job has
    // run) or any other single endpoint failing shouldn't blank out the
    // rest of the dashboard, which forkJoin's default all-or-nothing
    // behaviour would do.
    return forkJoin({
      portfolio: this.api.getPortfolio().pipe(catchError(() => of(null))),
      positions: this.api.getPositions().pipe(catchError(() => of([]))),
      signals: this.api.getSignalsToday().pipe(catchError(() => of(null))),
      status: this.api.getStatus().pipe(catchError(() => of(null))),
      regime: this.api.getCurrentRegime().pipe(catchError(() => of(null))),
    }).pipe(
      tap((data) => {
        this.portfolioSubject.next(data.portfolio);
        this.positionsSubject.next(data.positions);
        this.signalsSubject.next(data.signals);
        this.statusSubject.next(data.status);
        this.regimeSubject.next(data.regime);
        this.lastUpdated.set(new Date());
        this.isLoading.set(false);
      }),
      catchError(() => {
        this.error.set('Failed to load data. Retrying...');
        this.isLoading.set(false);
        return of(null);
      }),
      map(() => void 0),
    );
  }

  refresh(): void {
    this.fetchAll().subscribe();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }
}
