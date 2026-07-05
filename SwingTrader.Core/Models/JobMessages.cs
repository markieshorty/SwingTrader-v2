namespace SwingTrader.Core.Models;

// AccountId-keyed (not UserId) so consumers can resolve the shared trading
// data/keys for the account directly, matching every other scoped entity.
public record ResearchJobMessage(int AccountId, string JobId, DateOnly TradeDate, DateTime ScheduledFor);

public record WatchlistJobMessage(int AccountId, string JobId, DateTime ScheduledFor);

public record ReportJobMessage(int AccountId, string JobId, DateOnly ReportDate);

public record ExecutionJobMessage(int AccountId, string JobId, DateOnly TradeDate);

public record MonitorJobMessage(int AccountId, string JobId, DateTime CycleTime);

public record RiskJobMessage(int AccountId, string JobId, DateOnly EvaluationDate);

public record RefinementJobMessage(int AccountId, string JobId, DateOnly EvaluationDate);
