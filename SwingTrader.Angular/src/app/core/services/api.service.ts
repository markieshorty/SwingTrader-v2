import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  AccountMemberDto,
  AdminActionLogDto,
  AdminJobFailureDto,
  AdminStatsDto,
  AdminUserOverviewDto,
  AdminUserSummaryDto,
  ApplyResultDto,
  EconomicLinkDto,
  InviteResultDto,
  InsightsDetailSectionDto,
  KeyStatusesDto,
  KeyTestResult,
  MonitoringDashboardDto,
  NextRunDto,
  NotificationRecipientDto,
  PortfolioDto,
  PositionDto,
  ReadinessReportDto,
  RefinementStatusDto,
  RegimeDto,
  RiskProfileDto,
  RunResultDto,
  SentimentArchiveStatsDto,
  SignalGroupDto,
  StatusDto,
  BacktestRunStatusDto,
  LabAnalyseRequestDto,
  LabAnalyseResponseDto,
  LabApplyRequestDto,
  LabDataStatusDto,
  StrategyLabRequestDto,
  StrategyLabResponseDto,
  StrategyWeightsDto,
  TradeApprovalDto,
  TradeDto,
  TradingConfigDto,
  UniverseSymbolDto,
  UpdateRiskProfileDto,
  WatchlistDto,
  WatchlistItemDto,
  WatchlistType,
} from '../models/dtos';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getPortfolio(): Observable<PortfolioDto> {
    return this.http.get<PortfolioDto>(`${this.baseUrl}/api/portfolio`);
  }

  getNextRuns(): Observable<NextRunDto[]> {
    return this.http.get<NextRunDto[]>(`${this.baseUrl}/api/jobs/next-runs`);
  }

  getPositions(): Observable<PositionDto[]> {
    return this.http.get<PositionDto[]>(`${this.baseUrl}/api/positions`);
  }

  getSignalsToday(): Observable<SignalGroupDto> {
    return this.http.get<SignalGroupDto>(`${this.baseUrl}/api/signals/today`);
  }

  // Every signal ever scored - read-only history, never used for buy decisions.
  getSignalsHistory(): Observable<SignalGroupDto> {
    return this.http.get<SignalGroupDto>(`${this.baseUrl}/api/signals/history`);
  }

  getRecentTrades(days = 30): Observable<TradeDto[]> {
    return this.http.get<TradeDto[]>(`${this.baseUrl}/api/trades/recent`, {
      params: { days },
    });
  }

  getStatus(): Observable<StatusDto> {
    return this.http.get<StatusDto>(`${this.baseUrl}/api/status`);
  }

  getRefinementStatus(): Observable<RefinementStatusDto> {
    return this.http.get<RefinementStatusDto>(`${this.baseUrl}/api/refinement/status`);
  }

  applyRefinement(suggestionId: number): Observable<ApplyResultDto> {
    return this.http.post<ApplyResultDto>(`${this.baseUrl}/api/refinement/apply`, {
      suggestionId,
    });
  }

  rejectRefinement(suggestionId: number, note: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/api/refinement/reject`, {
      suggestionId,
      note,
    });
  }

  getReadiness(): Observable<ReadinessReportDto> {
    return this.http.get<ReadinessReportDto>(`${this.baseUrl}/api/readiness`);
  }

  completeChecklist(checkName: string, notes?: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/api/readiness/complete-checklist`, {
      checkName,
      notes,
    });
  }

  runAgent(agent: string): Observable<RunResultDto> {
    return this.http.post<RunResultDto>(`${this.baseUrl}/run/${agent}`, {});
  }

  approve(token: string, symbols?: string): Observable<unknown> {
    const params: Record<string, string> = symbols ? { token, symbols } : { token };
    return this.http.get(`${this.baseUrl}/approve`, { params });
  }

  getCurrentRegime(): Observable<RegimeDto> {
    return this.http.get<RegimeDto>(`${this.baseUrl}/api/refinement/current-regime`);
  }

  getAdminMe(): Observable<{ isAdmin: boolean }> {
    return this.http.get<{ isAdmin: boolean }>(`${this.baseUrl}/api/admin/me`);
  }

  getAdminStats(): Observable<AdminStatsDto> {
    return this.http.get<AdminStatsDto>(`${this.baseUrl}/api/admin/stats`);
  }

  validateStrategyLab(request: StrategyLabRequestDto): Observable<{ backtestRunId: number }> {
    return this.http.post<{ backtestRunId: number }>(`${this.baseUrl}/api/strategy-lab/validate`, request);
  }

  monteCarloStrategyLab(request: StrategyLabRequestDto): Observable<{ backtestRunId: number }> {
    return this.http.post<{ backtestRunId: number }>(`${this.baseUrl}/api/strategy-lab/montecarlo`, request);
  }

  getSentimentArchiveStats(): Observable<SentimentArchiveStatsDto> {
    return this.http.get<SentimentArchiveStatsDto>(`${this.baseUrl}/api/admin/sentiment-archive`);
  }

  getAdminUsers(): Observable<AdminUserSummaryDto[]> {
    return this.http.get<AdminUserSummaryDto[]>(`${this.baseUrl}/api/admin/users`);
  }

  adminApproveUser(userId: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/admin/users/${userId}/admin-approve`, {});
  }

  suspendUser(userId: string, reason?: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/admin/users/${userId}/suspend`, { reason });
  }

  unsuspendUser(userId: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/admin/users/${userId}/unsuspend`, {});
  }

  resetUserOnboarding(userId: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/admin/users/${userId}/reset-onboarding`, {});
  }

  forceUserDemo(userId: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/admin/users/${userId}/force-demo`, {});
  }

  deleteAdminUser(userId: string): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/admin/users/${userId}`);
  }

  getAdminJobFailures(): Observable<AdminJobFailureDto[]> {
    return this.http.get<AdminJobFailureDto[]>(`${this.baseUrl}/api/admin/jobs/failures`);
  }

  retryAdminJob(jobLogId: number): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/admin/jobs/retry`, { jobLogId });
  }

  deleteAdminJobFailure(jobLogId: number): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/admin/jobs/${jobLogId}`);
  }

  getAdminUserOverview(userId: string): Observable<AdminUserOverviewDto> {
    return this.http.get<AdminUserOverviewDto>(`${this.baseUrl}/api/admin/users/${userId}/overview`);
  }

  getAdminLogs(): Observable<AdminActionLogDto[]> {
    return this.http.get<AdminActionLogDto[]>(`${this.baseUrl}/api/admin/logs`);
  }

  runStrategyLab(request: StrategyLabRequestDto): Observable<StrategyLabResponseDto> {
    return this.http.post<StrategyLabResponseDto>(`${this.baseUrl}/api/strategy-lab/run`, request);
  }

  runStrategyLabHistoric(request: StrategyLabRequestDto): Observable<{ backtestRunId: number }> {
    return this.http.post<{ backtestRunId: number }>(`${this.baseUrl}/api/strategy-lab/run`, request);
  }

  getBacktestRun(id: number): Observable<BacktestRunStatusDto> {
    return this.http.get<BacktestRunStatusDto>(`${this.baseUrl}/api/strategy-lab/backtest/${id}`);
  }

  // 404s when the account has never completed a run of this mode.
  getLatestBacktestRun(mode: 'sweep' | 'ab' | 'validate' | 'montecarlo'): Observable<BacktestRunStatusDto> {
    return this.http.get<BacktestRunStatusDto>(`${this.baseUrl}/api/strategy-lab/backtest/latest?mode=${mode}`);
  }

  getLabDataStatus(): Observable<LabDataStatusDto> {
    return this.http.get<LabDataStatusDto>(`${this.baseUrl}/api/strategy-lab/data-status`);
  }

  syncLabData(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/strategy-lab/sync-data`, {});
  }

  // Queues the optimizer sweep (candidates around production weights, train/
  // holdout validated). Poll the returned run id like any historic run.
  runStrategyLabOptimize(): Observable<{ backtestRunId: number }> {
    return this.http.post<{ backtestRunId: number }>(`${this.baseUrl}/api/strategy-lab/optimize`, {});
  }

  // Claude analysis of a completed run — advisory only.
  analyseStrategyLabRun(request: LabAnalyseRequestDto): Observable<LabAnalyseResponseDto> {
    return this.http.post<LabAnalyseResponseDto>(`${this.baseUrl}/api/strategy-lab/analyse`, request);
  }

  // Apply Lab dials to production with a full audit trail (refinement history).
  applyLabConfig(request: LabApplyRequestDto): Observable<{ success: boolean; suggestionId: number; weightsId: number }> {
    return this.http.post<{ success: boolean; suggestionId: number; weightsId: number }>(
      `${this.baseUrl}/api/strategy-lab/apply`, request);
  }

  getMonitoringDashboard(): Observable<MonitoringDashboardDto> {
    return this.http.get<MonitoringDashboardDto>(`${this.baseUrl}/api/admin/monitoring`);
  }

  getMonitoringInsightsDetail(kind: string): Observable<InsightsDetailSectionDto> {
    return this.http.get<InsightsDetailSectionDto>(`${this.baseUrl}/api/admin/monitoring/insights/${kind}`);
  }

  createInvite(email: string, appBaseUrl: string): Observable<InviteResultDto> {
    return this.http.post<InviteResultDto>(`${this.baseUrl}/api/account/invites`, {
      email,
      appBaseUrl,
    });
  }

  getMembers(): Observable<AccountMemberDto[]> {
    return this.http.get<AccountMemberDto[]>(`${this.baseUrl}/api/account/members`);
  }

  removeMember(userId: string): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/account/members/${userId}`);
  }

  approveMember(userId: string): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/members/${userId}/approve`, {});
  }

  getApprovalStatus(): Observable<{ isApproved: boolean; adminApproved: boolean }> {
    return this.http.get<{ isApproved: boolean; adminApproved: boolean }>(`${this.baseUrl}/api/account/approval-status`);
  }

  getKeyStatuses(): Observable<KeyStatusesDto> {
    return this.http.get<KeyStatusesDto>(`${this.baseUrl}/api/keys`);
  }

  // test=false saves without a connectivity check - used when a follow-up
  // call will test (e.g. saving a Trading212 pair then hitting Connect), to
  // avoid redundant back-to-back broker calls that trip its rate limit.
  saveKey(provider: string, value: string, test = true): Observable<KeyTestResult> {
    return this.http.post<KeyTestResult>(`${this.baseUrl}/api/keys/${provider}?test=${test}`, { value });
  }

  testKey(provider: string): Observable<KeyTestResult> {
    return this.http.get<KeyTestResult>(`${this.baseUrl}/api/keys/${provider}/test`);
  }

  // Tests a whole Trading212 pair for one mode (the "Connect to demo/live"
  // buttons) - returns the account balance + environment.
  testTrading212Pair(mode: 'Demo' | 'Live'): Observable<KeyTestResult> {
    return this.http.get<KeyTestResult>(`${this.baseUrl}/api/keys/trading212/${mode}/test`);
  }

  deleteKey(provider: string): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/keys/${provider}`);
  }

  getAccountSettings(): Observable<TradingConfigDto & { globalRefinementOptIn: boolean; role: 'Owner' | 'Member' }> {
    return this.http.get<TradingConfigDto & { globalRefinementOptIn: boolean; role: 'Owner' | 'Member' }>(
      `${this.baseUrl}/api/account`,
    );
  }

  updateTradingConfig(tradingMode: string, approvalRequired: boolean, force = false): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/trading-config`, { tradingMode, approvalRequired, force });
  }

  // Pause / resume new-position executions for the account's current mode.
  setExecutionPaused(paused: boolean): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/execution-paused/${paused}`, {});
  }

  updateMyEmail(email: string): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/me/email`, { email });
  }

  getMe(): Observable<{ hasConfirmedEmail: boolean; email: string; displayName: string }> {
    return this.http.get<{ hasConfirmedEmail: boolean; email: string; displayName: string }>(
      `${this.baseUrl}/api/account/me`,
    );
  }

  getApprovals(): Observable<TradeApprovalDto[]> {
    return this.http.get<TradeApprovalDto[]>(`${this.baseUrl}/api/approvals`);
  }

  approveTradeApproval(id: number, symbols?: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/approvals/${id}/approve`, { symbols: symbols ?? null });
  }

  getStrategyWeights(): Observable<StrategyWeightsDto> {
    return this.http.get<StrategyWeightsDto>(`${this.baseUrl}/api/strategy-weights`);
  }

  updateStrategyWeights(weights: StrategyWeightsDto): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/strategy-weights`, weights);
  }

  getUniverse(): Observable<UniverseSymbolDto[]> {
    return this.http.get<UniverseSymbolDto[]>(`${this.baseUrl}/api/watchlists/universe`);
  }

  getWatchlists(): Observable<WatchlistDto[]> {
    return this.http.get<WatchlistDto[]>(`${this.baseUrl}/api/watchlists`);
  }

  getEconomicLinks(symbol: string): Observable<EconomicLinkDto[]> {
    return this.http.get<EconomicLinkDto[]>(`${this.baseUrl}/api/watchlists/links/${symbol}`);
  }

  setEconomicLinkSuppressed(linkId: number, suppressed: boolean): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/api/watchlists/links/${linkId}/suppress?suppressed=${suppressed}`, {});
  }

  createWatchlist(name: string, type: WatchlistType, description?: string): Observable<WatchlistDto> {
    return this.http.post<WatchlistDto>(`${this.baseUrl}/api/watchlists`, { name, type, description });
  }

  updateWatchlist(id: number, name: string, description?: string, topMoversEnabled?: boolean): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/watchlists/${id}`, { name, description, topMoversEnabled });
  }

  deleteWatchlist(id: number): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/watchlists/${id}`);
  }

  enableWatchlist(id: number): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/watchlists/${id}/enable`, {});
  }

  disableWatchlist(id: number): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/watchlists/${id}/disable`, {});
  }

  setDefaultWatchlist(id: number): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/watchlists/${id}/set-default`, {});
  }

  addWatchlistSymbol(id: number, symbol: string): Observable<WatchlistItemDto> {
    return this.http.post<WatchlistItemDto>(`${this.baseUrl}/api/watchlists/${id}/symbols`, { symbol });
  }

  removeWatchlistSymbol(id: number, symbol: string): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/watchlists/${id}/symbols/${symbol}`);
  }

  setForceIntoFinalList(watchlistId: number, symbol: string, force: boolean): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/api/watchlists/${watchlistId}/symbols/${symbol}/force`, { force });
  }

  getRiskProfile(): Observable<RiskProfileDto> {
    return this.http.get<RiskProfileDto>(`${this.baseUrl}/api/risk-profile`);
  }

  updateRiskProfile(profile: UpdateRiskProfileDto): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/risk-profile`, profile);
  }

  resetRiskProfile(): Observable<RiskProfileDto> {
    return this.http.post<RiskProfileDto>(`${this.baseUrl}/api/risk-profile/reset`, {});
  }

  setGlobalRefinementOptIn(enabled: boolean): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/global-refinement-optin/${enabled}`, {});
  }

  getNotificationRecipients(): Observable<NotificationRecipientDto[]> {
    return this.http.get<NotificationRecipientDto[]>(`${this.baseUrl}/api/account/notifications`);
  }

  addNotificationRecipient(email: string, categories: number): Observable<NotificationRecipientDto> {
    return this.http.post<NotificationRecipientDto>(`${this.baseUrl}/api/account/notifications`, {
      email,
      categories,
    });
  }

  removeNotificationRecipient(id: number): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/account/notifications/${id}`);
  }

  setTradeApproval(id: number, enabled: boolean): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/notifications/${id}/trade-approval`, { enabled });
  }

  deleteAccount(): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/account`);
  }
}
