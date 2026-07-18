namespace SwingTrader.Core.Models;

// AccountId-keyed (not UserId) so consumers can resolve the shared trading
// data/keys for the account directly, matching every other scoped entity.
// JobType distinguishes the morning run ("Research") from the optional midday
// rescore ("ResearchMidday"). It is the job-log dedup key, so the two runs
// dedup independently while sharing the same queue and consumer. The default
// keeps older queued messages (serialized without the property)
// deserializing as the morning run.
// ForceRescore: manual /run triggers set true (a deliberate refresh must
// re-score everything); scheduler messages leave it false so a Service Bus
// REDELIVERY of an interrupted run (host restart or deploy mid-run - observed
// 14 Jul 2026 as three duplicate ~90-min research runs in one day) RESUMES by
// skipping symbols already scored for the TradeDate instead of re-paying the
// full Claude cost from scratch. Defaulted so older queued messages still
// deserialize.
public record ResearchJobMessage(int AccountId, string JobId, DateOnly TradeDate, DateTime ScheduledFor, string JobType = "Research", bool ForceRescore = false);

public record WatchlistJobMessage(int AccountId, string JobId, DateTime ScheduledFor);

public record ReportJobMessage(int AccountId, string JobId, DateOnly ReportDate);

public record ExecutionJobMessage(int AccountId, string JobId, DateOnly TradeDate);

// BalanceOnly = the off-hours slimline variant: just fetch the T212 account
// summary and refresh the portfolio snapshot (no position checks, no exits).
// Keeps dashboards populated on evenings/weekends - without it, an account
// created outside market hours showed no balance until Monday's first monitor
// cycle. Defaulted so in-flight messages from before the field deserialize
// as full cycles.
public record MonitorJobMessage(int AccountId, string JobId, DateTime CycleTime, bool BalanceOnly = false);


public record RefinementJobMessage(int AccountId, string JobId, DateOnly EvaluationDate);

public record CandleSyncJobMessage(int AccountId, string JobId);

// Platform-level like CandleSync: one run refreshes the shared Filings /
// FilingDeltas tables for every account (docs/filing-delta-plan).
public record FilingSyncJobMessage(int AccountId, string JobId);

// Platform-level daily bellwether news scoring into the shared sentiment
// archive (docs/second-hop-plan).
public record BellwetherSyncJobMessage(int AccountId, string JobId);

public record BacktestJobMessage(int AccountId, string JobId, int BacktestRunId);
