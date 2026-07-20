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
  unrealizedPnl: number;
  unrealizedPnlPercent: number;
  winRate30d: number;
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
  // Actual money: share price × quantity, converted at the trade's own
  // entry FX rate where known (falls back to the current market rate).
  stopLossValueGbp: number;
  currentValueGbp: number;
  targetValueGbp: number;
  trailingStopValueGbp: number | null;
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
  // The contract this position runs under - rules frozen at buy time.
  // Null = trade placed before the freeze existed (falls back to profile).
  maxHoldDaysAtEntry: number | null;
  minHoldDaysAtEntry: number | null;
  momentumHealthThresholdAtEntry: number | null;
  trailingActivationPctAtEntry: number | null;
  trailingDistancePctAtEntry: number | null;
  forwardScoreAtEntry: number | null;
  sizeMultiplier: number | null;
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
  // Funnel shadow scores (Phase F1) - computed alongside the legacy
  // conviction but driving nothing yet. Null on signals scored before F1.
  gateScore: number | null;
  forwardScore: number | null;
  forwardScoreDegraded: boolean;
  wouldPassGate: boolean;
  wouldBeVetoed: boolean;
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
  maxHoldDaysAtEntry: number | null;
  minHoldDaysAtEntry: number | null;
  momentumHealthThresholdAtEntry: number | null;
  trailingActivationPctAtEntry: number | null;
  trailingDistancePctAtEntry: number | null;
  forwardScoreAtEntry: number | null;
  sizeMultiplier: number | null;
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
  origin: 'AutoRefinement' | 'StrategyLab' | 'SharedStrategy';
  isShadowMode: boolean;
  marketAdjustedWinRate: number;
  unusualMarketConditions: boolean;
  marketConditionWarning: string | null;
  replayCurrentAvgReturnPct: number | null;
  replaySuggestedAvgReturnPct: number | null;
  replayTradesKept: number | null;
  replayCheckPassed: boolean | null;
  // The risk half of a Strategy Lab apply (null for weight-only suggestions).
  suggestedRiskRules: RefinementRiskRulesDto | null;
}

// Risk-setting overrides recorded on a suggestion: which book they landed on,
// an optional autopause change, and the run's rule overrides (each field null
// when that dial wasn't part of the winning config).
export interface RefinementRiskRulesDto {
  targetRegime: string;
  autopause: boolean | null;
  rules: {
    excludedSetups?: string[] | null;
    maxHoldDays?: number | null;
    maxOpenPositions?: number | null;
    trailingActivationPct?: number | null;
    trailingDistancePct?: number | null;
    stopLossPct?: number | null;
    targetPct?: number | null;
    simulateProbation?: boolean | null;
    minHoldDays?: number | null;
    momentumHealthThreshold?: number | null;
    positionFraction?: number | null;
    lockedCapitalPct?: number | null;
    setupTactics?: {
      setup: string;
      stopLossPct: number;
      targetPct: number;
      guideHoldDays: number;
      trailingActivationPct: number;
      trailingDistancePct: number;
    }[] | null;
  } | null;
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
  executionPauseReason: 'Manual' | 'CircuitBreaker' | 'RegimeAutopause';
  executionPausedAt: string | null;
  role: 'Owner' | 'Member';
}

export interface StrategyWeightsDto {
  // The six GATE weights (sum to 1) that decide Buy/Watch/Hold/Avoid.
  rsiWeight: number;
  macdWeight: number;
  volumeWeight: number;
  setupQualityWeight: number;
  relativeStrengthWeight: number;
  priceLevelWeight: number;
  // The forward-score blend (sum to 1) that drives sizing/veto.
  forwardSentimentWeight: number;
  forwardFundamentalWeight: number;
  forwardFilingWeight: number;
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
  adminApproved: boolean;
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
  buysToday: number;
  exitsToday: number;
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
  setupQuality: number;
  relativeStrength: number;
  priceLevel: number;
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
  positionFraction: number | null;   // flat sizing: fraction of equity per trade
  lockedCapitalPct: number | null;   // reserve as fraction of account; total deployment <= 1 - this
  activeCapitalPct: number | null;   // sim-only "capital pool" mode: pool as fraction of the whole account
  maxPositionPctOfActive: number | null; // sim-only: per-position share of the pool
  // Per-setup entry/exit tactics (Phase 4). null = use the account's live
  // SetupTactics unchanged (untouched run mirrors live). When the tactics
  // editor is touched it sends the FULL edited set.
  setupTactics: LabSetupTacticsOverrideDto[] | null;
}

export interface LabSetupTacticsOverrideDto {
  setup: string;                 // SetupType name
  stopLossPct: number;
  targetPct: number;
  guideHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
}

export interface StrategyLabRequestDto {
  dataSource: 'own' | 'historic';
  weights: LabWeightsDto;
  buyThreshold: number;
  excludeBreakout: boolean;
  compareBaseline?: boolean; // A/B: also evaluate production dials over the same data
  autopauseDuringBear?: boolean; // historic: skip entries while SPY < 200dma (mirrors live autopause)
  rules?: LabTradingRulesDto | null; // historic: trading-rule experiment overrides
  regimeMode?: string | null; // historic: 'neutral'|'bull'|'bear'|'crisis'|'mixed' envelope both columns run under
  regimeOverrides?: Record<string, RegimeExposureOverrideDto> | null; // historic: per-regime exposure override, user column
}

// Per-regime exposure override for the user column (null field = inherit that
// book). Forced regime uses only autopause; Mixed uses all four (the 3 forms).
export interface RegimeExposureOverrideDto {
  autopause?: boolean | null;
  lockedCapitalPct?: number | null;   // fraction
  positionFraction?: number | null;   // fraction
  maxOpenPositions?: number | null;
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
  avgReturnPct: number; // the bucket's expectancy (mean return over all trades)
  avgHoldDays: number;  // mean calendar days held (0 for pre-existing stored runs)
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
  calmarRatio: number; // annualised return / max drawdown (0 for pre-existing stored runs)
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
  // The score the candidate was actually ranked on: worse train-window
  // half, discounted to a lower confidence bound. More pessimistic than
  // adjustedExpectancyPct by design.
  robustScorePct: number;
  // Full set of setups the candidate excluded (SetupType names) — restored by
  // "Test winner in A/B". Absent on runs stored before this field existed.
  excludedSetups?: string[];
  // The candidate's rule overrides (uniform + per-setup tactics), so "Test
  // winner in A/B" reproduces the winner faithfully rather than dropping them.
  rules?: LabTradingRulesDto | null;
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
  // Head-to-head: the best eligible candidate each search pool found, and
  // which one actually produced the winner above.
  bestTraditional: SweepCandidateDto | null;
  bestMlSearch: SweepCandidateDto | null;
  winnerSource: string | null;
}

export interface ValidateResultDto {
  mode: 'validate';
  validation: SweepValidationDto;
}

export interface MonteCarloResultDto {
  mode: 'montecarlo';
  resamples: number;
  trades: number;
  positionFraction: number;
  actualTotalReturnPct: number;
  actualMaxDrawdownPct: number;
  actualCalmarRatio: number;
  spyReturnPct: number;
  medianTotalReturnPct: number;
  p5TotalReturnPct: number;
  p95TotalReturnPct: number;
  medianMaxDrawdownPct: number;
  p95MaxDrawdownPct: number;
  probabilityOfLossPct: number;
  probabilityBeatingSpyPct: number;
  verdict: string;
}

// Setup-contribution (leave-one-out ablation): each setup's marginal effect on
// the production strategy, measured out-of-sample. Marginal = baseline − without;
// positive means the setup ADDS edge, negative means it's a DRAG.
export interface SetupAblationRowDto {
  setup: string;
  marginalTrainAdj: number;    // baseline − without, on the train window
  marginalHoldoutAdj: number;  // baseline − without, on the held-out window (trust this)
  holdoutAdjWithout: number;   // the strategy's held-out expectancy with the setup removed
  holdoutTradesWithout: number;
  holdoutMaxDrawdownWithout: number;
  consistent: boolean;         // same sign on both windows = trustworthy verdict
}

export interface SetupAblationDto {
  mode: 'ablation';
  baselineTrainAdjustedPct: number;
  baselineHoldoutAdjustedPct: number;
  baselineHoldoutTrades: number;
  baselineHoldoutMaxDrawdownPct: number;
  setups: SetupAblationRowDto[];
}

// Regime comparison: the production strategy over the full period under each
// regime book forced throughout, plus a Mixed run that switches book by the
// regime detected at each day. Answers "one master ruleset, or a regime mix?"
export interface RegimeComparisonRowDto {
  mode: string;            // "Force Bull" … "Force Crisis", "Mixed (regime-switch)"
  trades: number;
  winRate: number;         // fraction
  expectancyPct: number;   // %/trade
  totalReturnPct: number;
  maxDrawdownPct: number;
  calmarRatio: number;
}

export interface RegimeComparisonDto {
  mode: 'regime';
  spyReturnPct: number;
  rows: RegimeComparisonRowDto[];
}

// Setup-combination search: every non-empty combination of the account's
// setups replayed over the full period with the live dials + governing risk
// book, ranked by market-adjusted expectancy. Answers "which mix of setups
// should I be trading?"
export interface SetupSearchRowDto {
  setups: string[];         // SetupType names in this combination
  setupCount: number;
  isCurrentLive: boolean;   // matches the setups enabled for live trading right now
  trades: number;
  winRate: number;          // fraction
  expectancyPct: number;    // %/trade
  adjustedPct: number;      // market-adjusted expectancy — the ranking metric
  totalReturnPct: number;
  maxDrawdownPct: number;
  calmarRatio: number;
}

export interface SetupSearchDto {
  mode: 'setupsearch';
  spyReturnPct: number;
  setupsAvailable: string[];
  rows: SetupSearchRowDto[]; // best-first by adjustedPct
}

export type BacktestResultDto = HistoricResultDto | AbResultDto | SweepResultDto | ValidateResultDto | MonteCarloResultDto | SetupAblationDto | RegimeComparisonDto | SetupSearchDto;

export interface BacktestRunStatusDto {
  id: number;
  status: 'Queued' | 'Running' | 'Completed' | 'Failed';
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  totalCandidates: number | null;
  completedCandidates: number | null;
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

export type WatchlistType = 'AiManaged' | 'Manual' | 'Mixed' | 'AiQualitative';

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

export type MarketRegimeName = 'Default' | 'Bull' | 'Neutral' | 'Bear' | 'Crisis';

export type SetupTypeName =
  | 'OversoldRecovery'
  | 'OversoldRecoveryLoose'
  | 'Breakout'
  | 'MomentumContinuation'
  | 'VolumeSpike'
  | 'TrendFollowing';

export interface SetupTacticsRowDto {
  setupType: SetupTypeName;
  enabled: boolean; // live trade-eligibility; off = detected + scored but Buys demote to Watch
  stopLossPct: number;
  targetPct: number;
  guideHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
}

export interface SetupTacticsDto {
  setups: SetupTacticsRowDto[];
  allowedRanges: {
    stopLossPct: RiskProfileRangeDto;
    targetPct: RiskProfileRangeDto;
    guideHoldDays: RiskProfileRangeDto;
    trailingActivationPct: RiskProfileRangeDto;
    trailingDistancePct: RiskProfileRangeDto;
  };
}

export interface UpdateSetupTacticsDto {
  setupType: SetupTypeName;
  enabled: boolean;
  stopLossPct: number;
  targetPct: number;
  guideHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
}

export interface RiskProfileDto {
  lockedCapitalPct: number;
  maxOpenPositions: number;
  dailyLossCircuitBreakerPct: number;
  maxHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
  earningsGateDays: number;
  minHoldDays: number;
  momentumHealthThreshold: number;
  // Which regime book this payload is, whether it auto-pauses entries, and the
  // account's currently-detected live regime (drives the "active book" badge).
  regime: MarketRegimeName;
  currentRegime: MarketRegimeName;
  regimeUpdatedAt: string | null;
  availableRegimes: MarketRegimeName[];
  enabled: boolean;              // Default book only: this book is the master override
  defaultRegimeEnabled: boolean; // the Default book is on (governs live) — drives the [LIVE] capsule
  autopauseTrading: boolean;
  stopLossPct: number;   // flat stop, fraction (0.05 = 5%) — replaced the per-setup table
  targetPct: number;     // flat take-profit, fraction — replaced the per-conviction table
  sizingMode: 'Flat' | 'Funnel';
  flatPositionPct: number; // fraction of the whole portfolio per position
  // Funnel F2: Forward-score size tilt strength (0 = off, sizes untouched).
  sizingAggressiveness: number;
  // Funnel F3: Forward-score floor under gate-passing Buys (0 = veto off).
  forwardVetoFloor: number;
  riskLabel: string;
  buyThreshold: number | null;
  watchThreshold: number | null;
  stopLossPctDefault: number | null;
  capitalBreakdown: {
    totalCapital: number;
    lockedCapital: number;
    activeCapital: number;
    maxPerTrade: number;
  } | null;
  allowedRanges: {
    lockedCapitalPct: RiskProfileRangeDto;
    maxOpenPositions: RiskProfileRangeDto;
    dailyLossCircuitBreakerPct: RiskProfileRangeDto;
    maxHoldDays: RiskProfileRangeDto;
    trailingActivationPct: RiskProfileRangeDto;
    trailingDistancePct: RiskProfileRangeDto;
    earningsGateDays: RiskProfileRangeDto;
    minHoldDays: RiskProfileRangeDto;
    momentumHealthThreshold: RiskProfileRangeDto;
    stopLossPct: RiskProfileRangeDto;
    targetPct: RiskProfileRangeDto;
    flatPositionPct: RiskProfileRangeDto;
    sizingAggressiveness: RiskProfileRangeDto;
    forwardVetoFloor: RiskProfileRangeDto;
  };
}

// Account-level target for the weekly AI-managed watchlist refresh (how many
// symbols Claude picks). Lives on the Watchlists page, not Risk Management.
export interface WatchlistTargetSizeDto {
  targetWatchlistSize: number;
  min: number;
  max: number;
}

// Account-level size for the weekly QUALITATIVE watchlist refresh (how many
// symbols Claude picks on narrative grounds). Separate from the technical
// target size above.
export interface QualitativeWatchlistSizeDto {
  qualitativeWatchlistSize: number;
  min: number;
  max: number;
}

export interface UpdateRiskProfileDto {
  lockedCapitalPct: number;
  maxOpenPositions: number;
  dailyLossCircuitBreakerPct: number;
  maxHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
  earningsGateDays: number;
  minHoldDays: number;
  momentumHealthThreshold: number;
  // Which regime book to save, and whether it auto-pauses entries.
  regime: MarketRegimeName;
  enabled: boolean; // Default book only: master override on/off
  autopauseTrading: boolean;
  stopLossPct: number;
  targetPct: number;
  sizingMode: 'Flat' | 'Funnel';
  flatPositionPct: number;
  sizingAggressiveness: number;
  forwardVetoFloor: number;
}

// Second-hop economic link (docs/second-hop-plan) - Claude-built, platform-
// level, human-auditable: rationale always present, suppressible per link.
export interface EconomicLinkDto {
  id: number;
  symbol: string;
  linkedName: string;
  linkedTicker: string | null;
  relation: 'Supplier' | 'Customer' | 'Competitor' | 'SharedChain';
  transmissionNote: string;
  strength: number;
  rationale: string;
  suppressed: boolean;
  builtAt: string;
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

// --- Intelligence page (docs/intelligence-page-plan) ---

export interface FilingDeltaRowDto {
  symbol: string;
  filedAt: string;
  filingType: string;
  direction: number;
  materiality: number;
  delta: number;
  categories: string[];
  summary: string | null;
  effectiveScore: number;
  isHeld: boolean;
  isActiveToday: boolean;
  edgarUrl: string;
}

// FD3: a rules-based distress flag (8-K delisting/bankruptcy/non-reliance or
// going-concern language). While active it blocks Buys and exits positions.
export interface DistressFlagRowDto {
  symbol: string;
  reason: string;
  source: string;
  filedAt: string;
  isActive: boolean;
  isHeld: boolean;
}

export interface FilingsIntelligenceDto {
  windowDays: number;
  filingsChecked: number;
  changed: number;
  unchanged: number;
  deltas: FilingDeltaRowDto[];
  distressFlags: DistressFlagRowDto[];
}

// ── Forward scorecard (Intelligence tab 3) ──────────────────────────────────
// The forward-side feedback loop: did the forward score / vetoes / shadow
// signals actually predict anything?
export interface ForwardBucketDto {
  band: string;
  trades: number;
  winRate: number | null;
  avgReturnPct: number | null;
}

export interface BlockedBuyRowDto {
  signalDate: string;
  symbol: string;
  source: string; // 'Forward veto' | 'Distress veto' | 'Setup disabled'
  forwardScore: number | null;
  counterfactualReturnPct: number | null; // what taking the entry would have returned
  exitReason: string | null;
  tradingDaysHeld: number | null;
  stillOpen: boolean;
}

export interface BlockedBuySummaryDto {
  source: string;
  blocked: number;
  replayed: number;
  avgReturnPct: number;
  totalReturnPct: number;
  wouldHaveWon: number;
}

export interface SignalCorrelationDto {
  signal: string;
  pairs: number;
  pearsonR: number | null;
  avgFwdReturnWhenNegative: number | null;
  avgFwdReturnWhenPositive: number | null;
}

export interface ForwardScorecardDto {
  windowDays: number;
  forwardBuckets: ForwardBucketDto[];
  blockedSummaries: BlockedBuySummaryDto[];
  blockedBuys: BlockedBuyRowDto[];
  correlations: SignalCorrelationDto[];
}

export interface SecondHopRowDto {
  signalDate: string;
  symbol: string;
  companyName: string | null;
  score: number;
  secondHopSummary: string | null;
}

export interface SecondHopIntelligenceDto {
  windowDays: number;
  transmissions: SecondHopRowDto[];
}

// --- Strategy Lab history (Optimizer History / A/B History tabs) ---

export interface HistoricWeightsDto {
  rsi: number;
  macd: number;
  volume: number;
  setupQuality: number;
  relativeStrength: number;
  priceLevel: number;
}

// The A/B run's experimental risk-rule overrides. Any null field means the run
// used the production value (so applying leaves the live value alone).
export interface HistoricRulesDto {
  excludedSetups: string[] | null;
  maxHoldDays: number | null;
  maxOpenPositions: number | null;
  trailingActivationPct: number | null;
  trailingDistancePct: number | null;
  stopLossPct: number | null;
  targetPct: number | null;
  simulateProbation: boolean | null;
  minHoldDays: number | null;
  momentumHealthThreshold: number | null;
  positionFraction: number | null;
  activeCapitalPct: number | null;
  maxPositionPctOfActive: number | null;
}

export interface BacktestHistoryStatsDto {
  trades: number;
  winRatePct: number;
  totalReturnPct: number;
  maxDrawdownPct: number;
  profitFactor: number;
  expectancyPct: number;
}

export interface BacktestHistoryItemDto {
  id: number;
  mode: 'ab' | 'sweep';
  completedAt: string | null;
  canApply: boolean;
  label: string | null;
  weights: HistoricWeightsDto | null;
  buyThreshold: number | null;
  rules: HistoricRulesDto | null;
  hasRiskOverrides: boolean;
  stats: BacktestHistoryStatsDto | null;
}

export interface BacktestApplyResultDto {
  success: boolean;
  weightsId: number | null;
  appliedWeights: boolean;
  appliedRisk: boolean;
}

// ---- Strategy sharing ----

export interface ShareValidateEvidenceDto {
  runId: number;
  completedAt: string | null;
  heldUp: boolean;
  verdict: string;
  holdoutAdjustedExpectancyPct: number;
  baselineHoldoutAdjustedExpectancyPct: number;
}

export interface ShareMonteCarloEvidenceDto {
  runId: number;
  completedAt: string | null;
  verdict: string;
  medianTotalReturnPct: number;
  p5TotalReturnPct: number;
  p95MaxDrawdownPct: number;
  probabilityOfLossPct: number;
  probabilityBeatingSpyPct: number;
}

export interface ShareSimEvidenceDto {
  runId: number;
  completedAt: string | null;
  totalReturnPct: number;
  spyReturnPct: number;
  winRate: number;
  trades: number;
  maxDrawdownPct: number;
}

export interface ShareEvidenceDto {
  sim: ShareSimEvidenceDto | null;
  validate: ShareValidateEvidenceDto | null;
  monteCarlo: ShareMonteCarloEvidenceDto | null;
}

export interface ShareSnapshotWeightsDto {
  rsiWeight: number;
  macdWeight: number;
  volumeWeight: number;
  setupQualityWeight: number;
  relativeStrengthWeight: number;
  priceLevelWeight: number;
  forwardSentimentWeight: number;
  forwardFundamentalWeight: number;
  forwardFilingWeight: number;
  buyThreshold: number;
  watchThreshold: number;
  stopLossPctDefault: number;
}

export interface ShareSnapshotRiskBookDto {
  regime: string;
  enabled: boolean;
  autopauseTrading: boolean;
  lockedCapitalPct: number;
  maxOpenPositions: number;
  dailyLossCircuitBreakerPct: number;
  maxHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
  earningsGateDays: number;
  minHoldDays: number;
  momentumHealthThreshold: number;
  stopLossPct: number;
  targetPct: number;
  sizingMode: string;
  flatPositionPct: number;
  sizingAggressiveness: number;
  forwardVetoFloor: number;
}

export interface ShareSnapshotSetupTacticDto {
  setupType: string;
  enabled: boolean;
  stopLossPct: number;
  targetPct: number;
  guideHoldDays: number;
  trailingActivationPct: number;
  trailingDistancePct: number;
}

export interface ShareSnapshotDto {
  weights: ShareSnapshotWeightsDto;
  riskBooks: ShareSnapshotRiskBookDto[];
  setupTactics: ShareSnapshotSetupTacticDto[];
}

export interface StrategyShareDto {
  id: number;
  senderName: string;
  message: string | null;
  sentAt: string;
  status: 'Sent' | 'Applied' | 'Dismissed';
  appliedAt: string | null;
  dismissedAt: string | null;
  revertedAt: string | null;
  canRevert: boolean;
  evidence: ShareEvidenceDto | null;
  snapshot: ShareSnapshotDto | null;
}

export interface StrategyShareCountDto {
  count: number;
  total: number;
}

export interface ShareRecipientDto {
  accountId: number;
  displayName: string;
  email: string;
}

export interface ShareHistoryItemDto {
  id: number;
  recipientAccountId: number;
  recipientName: string;
  sentAt: string;
  status: string;
  appliedAt: string | null;
  revertedAt: string | null;
}

export interface ShareAdminStatusDto {
  fingerprint: string;
  sim: ShareSimEvidenceDto | null;
  validate: ShareValidateEvidenceDto | null;
  monteCarlo: ShareMonteCarloEvidenceDto | null;
  canSend: boolean;
  recipients: ShareRecipientDto[];
  history: ShareHistoryItemDto[];
}

export interface SendShareResultDto {
  success: boolean;
  sent: { shareId: number; accountId: number; recipient: string }[];
}

export interface MarketStatusDto {
  isOpen: boolean;
  changesAtUtc: string; // close time when open, next open time when closed
}

export interface ActiveJobDto {
  kind: 'backtest' | 'worker';
  label: string;
  status: 'Queued' | 'Running' | 'Completed' | 'Failed';
  startedAt: string | null;
  completedAt: string | null;
  progressCompleted: number | null;
  progressTotal: number | null;
  // Live stage breadcrumb for long worker runs (watchlist refresh stages).
  detail: string | null;
}

export interface ActiveJobsDto {
  jobs: ActiveJobDto[];
}
