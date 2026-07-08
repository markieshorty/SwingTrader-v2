// Hand-written to match the C# DTOs described in the API spec. These
// endpoints don't exist yet (Agents/Infrastructure business logic lands
// in a later phase) — once they're implemented, regenerate this file from
// /swagger/v1/swagger.json via `npm run generate-api` and delete this file.

export interface PortfolioDto {
  totalCapital: number;
  lockedCapital: number;
  reserveCapital: number;
  activeCapital: number;
  cashAvailable: number;
  openPositionsValue: number;
  totalPnl: number;
  todayPnl: number;
  todayPnlPercent: number;
  winRate30d: number;
  currentTier: string;
}

export interface PositionDto {
  id: number;
  symbol: string;
  companyName: string;
  entryPrice: number;
  currentPrice: number;
  stopLoss: number;
  target: number;
  trailingStopPrice: number | null;
  quantity: number;
  unrealisedPnl: number;
  unrealisedPnlPercent: number;
  daysHeld: number;
  entryDate: string;
  setupType: string;
  convictionScoreAtEntry: number | null;
  marketRegimeAtEntry: string | null;
  isNearStop: boolean;
  isNearTarget: boolean;
  phase: 'Probation' | 'Confirmed' | 'Exiting';
  momentumHealthScore: number | null;
  momentumHealthVerdict: string | null;
  momentumHealthReasoning: string | null;
  momentumHealthCheckedAt: string | null;
  phaseConfirmedAt: string | null;
}

export interface SignalDto {
  id: number;
  symbol: string;
  companyName: string;
  convictionScore: number | null;
  recommendation: 'Buy' | 'Watch' | 'Hold' | 'Avoid' | 'Sell';
  setupType: string;
  currentPrice: number;
  rsi14: number | null;
  volumeRatio: number | null;
  sentimentScore: number | null;
  relativeReturn: number | null;
  rsiScore: number | null;
  macdScore: number | null;
  volumeScore: number | null;
  sentimentComponentScore: number | null;
  setupQualityScore: number | null;
  relativeStrengthScore: number | null;
  priceLevelScore: number | null;
  fundamentalMomentumScore: number | null;
  fundamentalNarrative: string | null;
  analystTrend: string | null;
  insiderActivity: string | null;
  daysUntilEarnings: number | null;
  daysSinceEarnings: number | null;
}

export interface SignalGroupDto {
  buy: SignalDto[];
  watch: SignalDto[];
  hold: SignalDto[];
  avoid: SignalDto[];
}

export interface TradeDto {
  id: number;
  symbol: string;
  direction: string;
  entryPrice: number;
  exitPrice: number | null;
  entryValueGbp: number | null;
  exitValueGbp: number | null;
  feesGbp: number | null;
  realizedPnl: number | null;
  realizedPnlPercent: number | null;
  daysHeld: number;
  status: string;
  setupType: string;
  convictionScoreAtEntry: number | null;
  marketRegimeAtEntry: string | null;
  openedAt: string;
  closedAt: string | null;
}

export interface ActivityLogDto {
  category: string;
  title: string;
  result: 'Success' | 'Warning' | 'Failed' | 'Info' | 'Skipped' | 'Started';
  message: string | null;
  occurredAt: string;
}

export interface StatusDto {
  status: string;
  timestamp: string;
  runs: ActivityLogDto[];
  requiresApproval: boolean;
  approvedToday: boolean;
}

export interface RegimeDto {
  regime: 'Bull' | 'Neutral' | 'Bear' | 'Crisis';
  detectedAt: string;
}

export interface ComponentFindingDto {
  componentName: string;
  currentWeight: number;
  winnerAvgScore: number;
  loserAvgScore: number;
  correlation: number;
  suggestedWeight: number;
  weightDelta: number;
  reasoning: string;
}

export interface RefinementSuggestionDto {
  id: number;
  generatedAt: string;
  analysisPeriodStart: string;
  analysisPeriodEnd: string;
  tradeCountAnalysed: number;
  winnerCount: number;
  loserCount: number;
  overallWinRate: number;
  currentWeights: Record<string, number>;
  suggestedWeights: Record<string, number>;
  componentFindings: ComponentFindingDto[];
  assessmentSummary: string | null;
  confidenceLevel: 'Low' | 'Medium' | 'High';
  status: 'Pending' | 'Applied' | 'Rejected' | 'Superseded';
  isShadowMode: boolean;
  marketAdjustedWinRate: number;
  unusualMarketConditions: boolean;
  marketConditionWarning: string | null;
}

export interface RefinementStatusDto {
  currentWeights: Record<string, number>;
  latestSuggestion: RefinementSuggestionDto | null;
  history: RefinementSuggestionDto[];
  minTradesRequired: number;
  tradesScoredSoFar: number;
}

export interface ApplyResultDto {
  success: boolean;
  message: string;
}

export interface FeatureCriterionDto {
  label: string;
  met: boolean;
}

export interface FeatureCardDto {
  featureName: string;
  status: 'NotReady' | 'Approaching' | 'Ready' | 'AlreadyEnabled' | 'NoDataRequirement';
  riskLevel: 'Low' | 'Medium' | 'High';
  criteria: FeatureCriterionDto[];
  assessment: string;
  estimatedReadyDateRange: string | null;
  actionHint: string;
}

export interface TrajectoryWeekDto {
  weekStarting: string;
  tradeCount: number;
  winRate: number;
  speedIndicator: 'Up' | 'Down' | 'Flat';
}

export interface MilestoneDto {
  label: string;
  estimatedDateRange: string | null;
  completed: boolean;
  status: 'Completed' | 'Estimated' | 'MarketDependent' | 'RequiresCode';
}

export interface ReadinessReportDto {
  maturityLevel: 'EarlyStage' | 'Developing' | 'Established' | 'Mature';
  scoredClosedTrades: number;
  observedWinRate: number;
  winRateConfidenceIntervalLow: number | null;
  winRateConfidenceIntervalHigh: number | null;
  features: FeatureCardDto[];
  regimeBullProgress: number;
  regimeNeutralProgress: number;
  regimeBearProgress: number;
  trajectory: TrajectoryWeekDto[];
  milestones: MilestoneDto[];
}

export interface RunResultDto {
  success: boolean;
  message: string;
}

export interface UserDto {
  userId: string;
  email: string;
  displayName: string;
}

export interface InviteResultDto {
  inviteUrl: string;
}

export interface AccountMemberDto {
  userId: string;
  email: string;
  displayName: string;
  role: 'Owner' | 'Member';
  lastLoginAt: string;
  isApproved: boolean;
}

export type KeyStatus = 'NotSet' | 'SetNotTested' | 'Valid' | 'Invalid';

export type ApiKeyProvider =
  | 'Finnhub'
  | 'Tiingo'
  | 'Trading212DemoKey'
  | 'Trading212DemoSecret'
  | 'Trading212LiveKey'
  | 'Trading212LiveSecret'
  | 'Claude';

export type KeyStatusesDto = Record<ApiKeyProvider, KeyStatus>;

export type TradingMode = 'Demo' | 'Live';

export interface TradingConfigDto {
  tradingMode: TradingMode;
  approvalRequired: boolean;
  t212AccountId: string | null;
  role: 'Owner' | 'Member';
}

export interface StrategyWeightsDto {
  rsiWeight: number;
  macdWeight: number;
  volumeWeight: number;
  sentimentWeight: number;
  setupQualityWeight: number;
  relativeStrengthWeight: number;
  priceLevelWeight: number;
  fundamentalMomentumWeight: number;
  buyThreshold: number;
  watchThreshold: number;
  stopLossPctDefault: number;
}

export interface NextRunDto {
  jobType: string;
  nextRunAtUtc: string;
  nextRunLabel: string;
}

export interface AdminStatsDto {
  totalUsers: number;
  activeUsersLast7Days: number;
  totalTradesAllTime: number;
  averageWinRateAllUsers: number;
  usersInDemoMode: number;
  usersInLiveMode: number;
  usersNotOnboarded: number;
  totalJobFailuresLast24h: number;
  totalAccounts: number;
}

export interface AdminUserSummaryDto {
  userId: string;
  email: string;
  displayName: string;
  role: 'Owner' | 'Member';
  accountId: number;
  firstLoginAt: string;
  lastLoginAt: string;
  isOnboarded: boolean;
  isApproved: boolean;
  isSuspended: boolean;
  suspendReason: string | null;
  tradingMode: 'Demo' | 'Live';
  totalTrades: number;
  winRate: number | null;
  riskLabel: string;
  enabledWatchlistCount: number;
  accountDeleted: boolean;
}

export interface AdminJobFailureDto {
  jobLogId: number;
  accountId: number;
  ownerEmail: string | null;
  jobType: string;
  jobDate: string;
  errorMessage: string | null;
  attemptCount: number;
}

export interface AdminActionLogDto {
  id: number;
  adminUserId: string;
  targetUserId: string;
  action: string;
  details: string | null;
  performedAt: string;
}

export type WatchlistType = 'AiManaged' | 'Manual' | 'Mixed';

export interface WatchlistItemDto {
  id: number;
  symbol: string;
  companyName: string;
  sector: string;
  isActive: boolean;
  notes: string | null;
}

export interface WatchlistDto {
  id: number;
  name: string;
  type: WatchlistType;
  isEnabled: boolean;
  isDefault: boolean;
  description: string | null;
  items: WatchlistItemDto[];
  topMoversEnabled: boolean;
}

export interface RiskProfileRangeDto {
  min: number;
  max: number;
}

export interface RiskProfileDto {
  lockedCapitalPct: number;
  maxPositionPctOfActive: number;
  maxOpenPositions: number;
  dailyLossCircuitBreakerPct: number;
  tier1UnlockMinTrades: number;
  tier1UnlockMinWinRate: number;
  tier2UnlockMinTrades: number;
  tier2UnlockMinWinRate: number;
  maxHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
  earningsGateDays: number;
  minHoldDays: number;
  momentumHealthThreshold: number;
  targetWatchlistSize: number;
  riskLabel: string;
  buyThreshold: number | null;
  watchThreshold: number | null;
  stopLossPctDefault: number | null;
  capitalBreakdown: {
    totalCapital: number;
    lockedCapital: number;
    activeCapital: number;
    maxPerTrade: number;
    currentTier: string;
  } | null;
  allowedRanges: {
    lockedCapitalPct: RiskProfileRangeDto;
    maxPositionPctOfActive: RiskProfileRangeDto;
    maxOpenPositions: RiskProfileRangeDto;
    dailyLossCircuitBreakerPct: RiskProfileRangeDto;
    tier1UnlockMinTrades: RiskProfileRangeDto;
    tier1UnlockMinWinRate: RiskProfileRangeDto;
    maxHoldDays: RiskProfileRangeDto;
    trailingActivationPct: RiskProfileRangeDto;
    trailingDistancePct: RiskProfileRangeDto;
    earningsGateDays: RiskProfileRangeDto;
    minHoldDays: RiskProfileRangeDto;
    momentumHealthThreshold: RiskProfileRangeDto;
    targetWatchlistSize: RiskProfileRangeDto;
  };
}

export interface UpdateRiskProfileDto {
  lockedCapitalPct: number;
  maxPositionPctOfActive: number;
  maxOpenPositions: number;
  dailyLossCircuitBreakerPct: number;
  tier1UnlockMinTrades: number;
  tier1UnlockMinWinRate: number;
  tier2UnlockMinTrades: number;
  tier2UnlockMinWinRate: number;
  maxHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
  earningsGateDays: number;
  minHoldDays: number;
  momentumHealthThreshold: number;
  targetWatchlistSize: number;
}

export interface NotificationRecipientDto {
  id: number;
  email: string;
  tradeApprovalEnabled: boolean;
}

export interface TradeApprovalCandidateDto {
  symbol: string;
  setupType: string;
  conviction: number | null;
  price: number;
  stop: number | null;
  target: number | null;
  riskReward: number | null;
}

export interface TradeApprovalDto {
  id: number;
  tradeDate: string;
  isApproved: boolean;
  approvedAt: string | null;
  approvedSymbols: string | null;
  approvedVia: string | null;
  candidates: TradeApprovalCandidateDto[];
}
