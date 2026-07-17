namespace SwingTrader.Core.Models;

// One strategy handed from one account's owner to another (docs: admin
// "Share Strategy" tab -> recipient's "Shared Strategies" page). AccountId
// (BaseEntity) is the RECIPIENT's account - the share lives in their tenant
// and is listed/applied under their auth. The snapshot is FROZEN at send
// time: later sender tweaks never change what the recipient reviews/applies.
public class StrategyShare : BaseEntity
{
    public int SenderAccountId { get; set; }
    public string SenderName { get; set; } = string.Empty;   // display, e.g. "Mark Short"
    public string? Message { get; set; }                     // optional note from the sender

    // StrategySnapshot (camelCase JSON): weights + thresholds, all regime
    // risk books, all setup tactics - everything except watchlists.
    public string SnapshotJson { get; set; } = string.Empty;

    // Fingerprint of the sender's live settings at send time; matches the
    // ConfigFingerprint stamped on the validate/MC evidence runs below.
    public string ConfigFingerprint { get; set; } = string.Empty;

    // ShareEvidence (camelCase JSON): the validate + Monte Carlo verdicts
    // quoted in the email, with run ids/dates - frozen alongside the snapshot.
    public string EvidenceJson { get; set; } = string.Empty;

    // "Sent" | "Applied" | "Dismissed"
    public string Status { get; set; } = "Sent";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime? DismissedAt { get; set; }

    // StrategySnapshot of the RECIPIENT's own settings, captured immediately
    // before apply - powers one-click "Restore my previous settings".
    public string? BackupJson { get; set; }
    public DateTime? RevertedAt { get; set; }
}
