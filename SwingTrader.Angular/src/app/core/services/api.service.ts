import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  AccountMemberDto,
  ApplyResultDto,
  InviteResultDto,
  KeyStatusesDto,
  NotificationRecipientDto,
  PortfolioDto,
  PositionDto,
  ReadinessReportDto,
  RefinementStatusDto,
  RegimeDto,
  RunResultDto,
  SignalGroupDto,
  StatusDto,
  StrategyWeightsDto,
  TradeDto,
  TradingConfigDto,
} from '../models/dtos';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getPortfolio(): Observable<PortfolioDto> {
    return this.http.get<PortfolioDto>(`${this.baseUrl}/api/portfolio`);
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

  getAccountSettings(): Observable<TradingConfigDto & { globalRefinementOptIn: boolean }> {
    return this.http.get<TradingConfigDto & { globalRefinementOptIn: boolean }>(`${this.baseUrl}/api/account`);
  }

  updateTradingConfig(tradingMode: string, approvalRequired: boolean): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/account/trading-config`, { tradingMode, approvalRequired });
  }

  getStrategyWeights(): Observable<StrategyWeightsDto> {
    return this.http.get<StrategyWeightsDto>(`${this.baseUrl}/api/strategy-weights`);
  }

  updateStrategyWeights(weights: StrategyWeightsDto): Observable<unknown> {
    return this.http.put(`${this.baseUrl}/api/strategy-weights`, weights);
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

  deleteAccount(): Observable<unknown> {
    return this.http.delete(`${this.baseUrl}/api/account`);
  }
}
