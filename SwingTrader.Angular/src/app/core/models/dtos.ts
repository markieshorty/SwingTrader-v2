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
  // Null for signals scored before StockSignal.CompanyName existed.
  companyName: string | null;
  signalDate: string; // used by the "View historic signals" toggle to show which day each card is from
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
  // Null for trades placed before Trade.CompanyName existed.
  companyName: string | null;
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

// Admin "view a user's account" overview — reuses the same portfolio/position/
// signal/trade shapes the owner sees on their own dashboard, plus their
// watchlists inline. Served by GET /api/admin/users/{userId}/overview.
export interface AdminWatchlistSymbolDto {
  symbol: string;
  companyName: string;
  sector: string;
  isActive: boolean;
}

export interface AdminWatchlistDto {
  id: number;
  name: string;
  type: string;
  isEnabled: boolean;
  isDefault: boolean;
  symbols: AdminWatchlistSymbolDto[];
}

export interface AdminUserOverviewDto {
  user: {
    userId: string;
    email: string;
    displayName: string;
    tradingMode: 'Demo' | 'Live';
    role: 'Owner' | 'Member';
    accountId: number;
  };
  portfolio: PortfolioDto | null;
  positions: PositionDto[];
  trades: TradeDto[];
  signals: SignalGroupDto;
  watchlists: AdminWatchlistDto[];
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
  // Which tool proposed this weight change - the refinement page is the one
  // audit trail for every production weight change, whatever its origin.
  origin: 'AutoRefinement' | 'StrategyLab';
  isShadowMode: boolean;
  marketAdjustedWinRate: number;
  unusualMarketConditions: boolean;
  marketConditionWarning: string | null;
  replayCurrentAvgReturnPct: number | null;
  replaySuggestedAvgReturnPct: number | null;
  replayTradesKept: number | null;
  replayCheckPassed: boolean | null;
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

// Inline connection indicator state for a Trading212 pair's connect button.
export type ConnectionStatus = 'idle' | 'connecting' | 'success' | 'error';
export interface ConnectionState {
  status: ConnectionStatus;
  text: string;
}

// Result of testing a key. For a complete Trading212 pair the extra fields
// come back from a real account call so the user can confirm the credentials
// are correct and pointed at the right (demo/live) environment.
export interface KeyTestResult {
  valid: boolean;
  message: string;
  isDemo: boolean | null;
  cashTotal: number | null;
  cashFree: number | null;
  currency: string | null;
}

export type TradingMode = 'Demo' | 'Live';

export interface TradingConfigDto {
  tradingMode: TradingMode;
  approvalRequired: boolean;
  t212AccountId: string | null;
  // Whether new-position executions ("entries") are paused for the current
  // mode, and — while paused — why and since when.
  executionPaused: boolean;
  executionPauseReason: 'Manual' | 'CircuitBreaker';
  executionPausedAt: string | null;
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

export interface SentimentArchiveStatsDto {
  scoreCount: number;
  articleCount: number;
  oldestScoreDate: string | null;
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

// ── Monitoring dashboard ──────────────────────────────────────────────────
export interface MonitoringWorkerDto {
  name: string;
  lastResult: string;
  lastHeartbeatAt: string;
  minutesSinceHeartbeat: number;
  isStale: boolean;
  message: string | null;
}

export interface QueueDepthDto {
  name: string;
  active: number;
  deadLettered: number;
  scheduled: number;
}

export interface QueueHealthSectionDto {
  available: boolean;
  error: string | null;
  queues: QueueDepthDto[];
  totalDeadLettered: number;
}

export interface MonitoringJobTypeCountDto {
  jobType: string;
  completed: number;
  failed: number;
}

export interface MonitoringJobsDto {
  failed24h: number;
  completed24h: number;
  processing: number;
  enqueued: number;
  byType: MonitoringJobTypeCountDto[];
}

export interface NamedCountDto {
  name: string;
  count: number;
}

export interface InsightsSectionDto {
  available: boolean;
  error: string | null;
  requests24h: number;
  failedRequestPct: number;
  serverExceptions24h: number;
  dependencyFailures24h: number;
  claudeRateLimited24h: number;
  topExceptions: NamedCountDto[];
}

export interface MonitoringSystemEventDto {
  occurredAt: string;
  accountId: number;
  title: string;
  result: string;
  message: string | null;
}

export interface MonitoringTradingStateDto {
  openPositions: number;
  pendingIntents: number;
  cancelledToday: number;
  ordersPlacedToday: number;
}

export interface MonitoringDashboardDto {
  generatedAt: string;
  workers: MonitoringWorkerDto[];
  queues: QueueHealthSectionDto;
  jobs: MonitoringJobsDto;
  recentJobFailures: AdminJobFailureDto[];
  insights: InsightsSectionDto;
  systemEvents: MonitoringSystemEventDto[];
  trading: MonitoringTradingStateDto;
}

export interface InsightsEventDto {
  timestamp: string;
  category: string;
  title: string;
  detail: string | null;
  operation: string | null;
  location: string | null;
}

export interface InsightsDetailSectionDto {
  available: boolean;
  error: string | null;
  kind: string;
  events: InsightsEventDto[];
}

export interface UniverseSymbolDto {
  symbol: string;
  companyName: string;
}

// ── Strategy Lab ──────────────────────────────────────────────────────────
export interface LabWeightsDto {
  rsi: number;
  macd: number;
  volume: number;
  sentiment: number;
  setupQuality: number;
  relativeStrength: number;
  priceLevel: number;
  fundamentalMomentum: number;
}

// Historic-mode experiment overrides of the trading RULES. Null field = the
// account's live risk-profile value. Applied to "Your dials" only — the
// production baseline always replays with live rules. Never touches settings.
export interface LabTradingRulesDto {
  excludedSetups: string[] | null; // SetupType names, e.g. ['Breakout','VolumeSpike']
  maxHoldDays: number | null;
  maxOpenPositions: number | null;
  trailingActivationPct: number | null; // fraction: 0.05 = arm at +5%
  trailingDistancePct: number | null;   // fraction: 0.03 = trail 3% below
  stopLossPct: number | null;   // flat stop override; null = production setup table
  targetPct: number | null;     // flat target override; null = production conviction table
  simulateProbation: boolean | null; // null = true (production always runs probation)
  minHoldDays: number | null;        // probation check day
  momentumHealthThreshold: number | null; // probation pass bar, 0..1
  positionFraction: number | null;   // legacy flat sizing: fraction of equity per trade
  activeCapitalPct: number | null;   // set = live-mirroring tier pool sizing (0.10 = Tier 1)
  maxPositionPctOfActive: number | null; // per-position share of the pool
}

export interface StrategyLabRequestDto {
  dataSource: 'own' | 'historic';
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  compareBaseline?: boolean; // A/B: also evaluate production dials over the same data
  autopauseDuringBear?: boolean; // historic: skip entries while SPY < 200dma (mirrors live autopause)
  rules?: LabTradingRulesDto | null; // historic: trading-rule experiment overrides
}

export interface LabResultDto {
  totalClosedTrades: number;
  tradesKept: number;
  tradesDropped: number;
  droppedWinners: number;
  droppedLosers: number;
  actualAvgReturnPct: number;
  simAvgReturnPct: number;
  actualWinRate: number;
  simWinRate: number;
  actualTotalPnl: number;
  simTotalPnl: number;
  summary: string;
}

export interface LabSuggestionDto {
  description: string;
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  simAvgReturnPct: number;
  simWinRate: number;
  tradesKept: number;
  improvementPct: number;
}

export interface LabTradeOutcomeDto {
  symbol: string;
  openedAt: string;
  conviction: number;
  setup: string;
  returnPct: number;
  wouldTake: boolean;
}

// Production dials evaluated over the same data as the user's run, for
// side-by-side comparison (weights snapshotted at evaluation time).
export interface LabBaselineDto {
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  result: LabResultDto;
}

export interface StrategyLabResponseDto {
  result: LabResultDto;
  suggestions: LabSuggestionDto[];
  trades: LabTradeOutcomeDto[];
  warning: string | null;
  baseline: LabBaselineDto | null;
}

export interface LabBucketStatDto {
  key: string;
  count: number;
  winRate: number;
  avgReturnPct: number;
}

export interface HistoricResultDto {
  from: string;
  to: string;
  trades: number;
  winRate: number;
  avgWinPct: number;
  avgLossPct: number;
  expectancyPct: number;
  profitFactor: number;
  totalReturnPct: number;
  maxDrawdownPct: number;
  spyReturnPct: number;
  bySetup: LabBucketStatDto[];
  byConviction: LabBucketStatDto[];
  byExitReason: LabBucketStatDto[];
}

// Historic A/B: both configs evaluated over the identical window, labelled
// with what was actually run (baseline snapshotted at queue time).
export interface AbCandidateDto {
  label: string;
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  autopauseDuringBear: boolean;
  result: HistoricResultDto;
}

export interface AbResultDto {
  mode: 'ab';
  candidates: AbCandidateDto[];
}

// Optimizer sweep: candidates evaluated on the train window (earlier ~70%),
// winner validated on the held-out remainder it never saw.
export interface SweepCandidateDto {
  label: string;
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  autopauseDuringBear: boolean;
  trades: number;
  winRate: number;
  expectancyPct: number;
  adjustedExpectancyPct: number;
  profitFactor: number;
  maxDrawdownPct: number;
  totalReturnPct: number;
  metConstraints: boolean;
  rejectionReason: string | null;
}

export interface SweepValidationDto {
  train: HistoricResultDto;
  holdout: HistoricResultDto;
  trainAdjustedExpectancyPct: number;
  holdoutAdjustedExpectancyPct: number;
  baselineHoldout: HistoricResultDto;
  baselineHoldoutAdjustedExpectancyPct: number;
  heldUp: boolean;
  verdict: string;
}

export interface SweepResultDto {
  mode: 'sweep';
  baseline: SweepCandidateDto;
  winner: SweepCandidateDto;
  validation: SweepValidationDto;
  candidates: SweepCandidateDto[];
  explanation: string | null;
}

export interface ValidateResultDto {
  mode: 'validate';
  validation: SweepValidationDto;
}

export type BacktestResultDto = HistoricResultDto | AbResultDto | SweepResultDto | ValidateResultDto;

export interface BacktestRunStatusDto {
  id: number;
  status: 'Queued' | 'Running' | 'Completed' | 'Failed';
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  result: BacktestResultDto | null;
}

// Claude "Analyse this run": advisory text + an optional next config worth
// testing. Never runs or applies anything itself.
export interface LabAnalyseOwnResultDto {
  totalClosedTrades: number;
  tradesKept: number;
  droppedWinners: number;
  droppedLosers: number;
  actualAvgReturnPct: number;
  simAvgReturnPct: number;
  actualWinRate: number;
  simWinRate: number;
}

export interface LabAnalyseRequestDto {
  dataSource: 'own' | 'historic';
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  ownResult: LabAnalyseOwnResultDto | null;
  backtestRunId: number | null;
  autopauseDuringBear?: boolean;
}

export interface LabAnalyseSuggestionDto {
  rationale: string;
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
}

export interface LabAnalyseResponseDto {
  analysis: string;
  suggestion: LabAnalyseSuggestionDto | null;
}

// Apply from the Lab: one click, but audited - records an immediately-applied
// RefinementSuggestion (origin StrategyLab) carrying the run's evidence.
export interface LabApplyRequestDto {
  weights: LabWeightsDto;
  buyThreshold: number;
  evidenceSummary: string;
  tradeCount: number;
  winRate: number;
  confidence: 0 | 1 | 2; // RefinementConfidenceLevel: Low | Medium | High
}

export interface LabDataStatusDto {
  bars: number;
  latestDate: string | null;
  platformKeyConfigured: boolean;
}

export type WatchlistType = 'AiManaged' | 'Manual' | 'Mixed';

export interface WatchlistItemDto {
  id: number;
  symbol: string;
  companyName: string;
  sector: string;
  isActive: boolean;
  notes: string | null;
  // Bypasses the stock screener and the parent watchlist's enabled state -
  // researched every trading day regardless.
  forceIntoFinalList: boolean;
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
  autopauseDuringBear: boolean;
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
    tier2UnlockMinTrades: RiskProfileRangeDto;
    tier2UnlockMinWinRate: RiskProfileRangeDto;
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
  autopauseDuringBear: boolean;
}

export interface NotificationRecipientDto {
  id: number;
  email: string;
  tradeApprovalEnabled: boolean;
}

export interface TradeApprovalCandidateDto {
  symbol: string;
  // Null for candidates from before StockSignal.CompanyName existed.
  companyName: string | null;
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
