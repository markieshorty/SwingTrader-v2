namespace SwingTrader.Core.Models;

// AccountId-keyed (not UserId) so consumers can resolve the shared trading
// data/keys for the account directly, matching every other scoped entity.
// JobType distinguishes the morning run ("Research") from the optional midday
// rescore ("ResearchMidday"). It is the job-log dedup key, so the two runs
// dedup independently while sharing the same queue and consumer. The default
// keeps older queued messages (serialized without the property)
// deserializing as the morning run.
public record ResearchJobMessage(int AccountId, string JobId, DateOnly TradeDate, DateTime ScheduledFor, string JobType = "Research");

public record WatchlistJobMessage(int AccountId, string JobId, DateTime ScheduledFor);

public record ReportJobMessage(int AccountId, string JobId, DateOnly ReportDate);

public record ExecutionJobMessage(int AccountId, string JobId, DateOnly TradeDate);

public record MonitorJobMessage(int AccountId, string JobId, DateTime CycleTime);

public record RiskJobMessage(int AccountId, string JobId, DateOnly EvaluationDate);

public record RefinementJobMessage(int AccountId, string JobId, DateOnly EvaluationDate);

public record ReadinessJobMessage(int AccountId, string JobId, DateOnly SnapshotDate);

public record CandleSyncJobMessage(int AccountId, string JobId);

// Platform-level like CandleSync: one run refreshes the shared Filings /
// FilingDeltas tables for every account (docs/filing-delta-plan).
public record FilingSyncJobMessage(int AccountId, string JobId);

public record BacktestJobMessage(int AccountId, string JobId, int BacktestRunId);
