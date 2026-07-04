namespace SwingTrader.Core.Models;

public class WorkerHeartbeat : BaseEntity
{
    public string WorkerName { get; set; } = "";
    public DateTime LastHeartbeatAt { get; set; }
    public string LastRunResult { get; set; } = "";  // "Success" / "Warning" / "Failed" / "Skipped"
    public string? LastRunMessage { get; set; }
}
