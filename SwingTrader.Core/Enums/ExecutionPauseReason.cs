namespace SwingTrader.Core.Enums;

// Why new-position executions are paused for a mode. Only meaningful while
// the mode is actually paused (Account.IsExecutionPaused).
public enum ExecutionPauseReason
{
    // The owner flipped the Settings > Trading pause switch by hand.
    Manual = 0,

    // The daily-loss circuit breaker tripped and auto-paused new entries;
    // stays paused until the owner reviews and manually resumes.
    CircuitBreaker = 1,
}
