namespace SwingTrader.Core.Models;

// Append-only audit trail of every admin action - never modified or deleted.
// Global across all accounts (the whole point of admin visibility), so this
// is an UnscopedEntity like AppUser/Account/AccountInvite rather than
// BaseEntity - there's no single Account it belongs to.
public class AdminActionLog : UnscopedEntity
{
    public string AdminUserId { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;

    // "Suspend", "Unsuspend", "ForceDemo", "ResetOnboarding", "DeleteUser",
    // "SendMessage", "RetryJob"
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime PerformedAt { get; set; }
}
