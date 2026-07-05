using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// One row per (AccountId, JobType, JobDate) - the Scheduler checks this
// before enqueuing so a Function restart or overlapping timer tick can't
// double-enqueue the same account's job for the same day.
public class JobLogEntry : BaseEntity
{
    public string JobType { get; set; } = string.Empty;
    public DateOnly JobDate { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Enqueued;
    public DateTime EnqueuedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; } = 1;
}
