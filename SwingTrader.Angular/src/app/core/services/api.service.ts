import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  AccountMemberDto,
  AdminActionLogDto,
  AdminJobFailureDto,
  AdminStatsDto,
  AdminUserSummaryDto,
  ApplyResultDto,
  InviteResultDto,
  KeyStatusesDto,
  NextRunDto,
  NotificationRecipientDto,
  PortfolioDto,
  PositionDto,
  ReadinessReportDto,
  RefinementStatusDto,
  RegimeDto,
  RiskProfileDto,
  RunResultDto,
  SignalGroupDto,
  StatusDto,
  StrategyWeightsDto,
  TradeApprovalDto,
  TradeDto,
  TradingConfigDto,
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

  getAdminUsers(): Observable<AdminUserSummaryDto[]> {
    return this.http.get<AdminUserSummaryDto[]>(`${this.baseUrl}/api/admin/users`);
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

  getAdminLogs(): Observable<AdminActionLogDto[]> {
    return this.http.get<AdminActionLogDto[]>(`${this.baseUrl}/api/admin/logs`);
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

  getApprovalStatus(): Observable<{ isApproved: boolean }> {
    return this.http.get<{ isApproved: boolean }>(`${this.baseUrl}/api/account/approval-status`);
  }

  getKeyStatuses(): Observable<KeyStatusesDto> {
    return this.http.get<KeyStatusesDto>(`${this.baseUrl}/api/keys`);
  }

  saveKey(provider: string, value: string): Observable<{ valid: boolean }> {
    return this.http.post<{ valid: boolean }>(`${this.baseUrl}/api/keys/${provider}`, { value });
  }

  testKey(provider: string): Observable<{ valid: boolean }> {
    return this.http.get<{ valid: boolean }>(`${this.baseUrl}/api/keys/${provider}/test`);
  }

  deleteKey(provider: string): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/keys/${provider}`);
  }

  getAccountSettings(): Observable<TradingConfigDto & { globalRefinementOptIn: boolean; role: 'Owner' | 'Member' }> {
    return this.http.get<TradingConfigDto & { globalRefinementOptIn: boolean; role: 'Owner' | 'Member' }>(
      `${this.baseUrl}/api/account`,
    );
  }

  updateTradingConfig(tradingMode: string, approvalRequired: boolean): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/trading-config`, { tradingMode, approvalRequired });
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

  getWatchlists(): Observable<WatchlistDto[]> {
    return this.http.get<WatchlistDto[]>(`${this.baseUrl}/api/watchlists`);
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
