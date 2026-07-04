namespace SwingTrader.Core.Models;

// Manual confirmation steps that cannot be auto-detected from other tables —
// e.g. "I physically compared the account ID against the T212 app." Persists
// across restarts; deliberately never stored in appsettings.json.
public class SystemChecklist : BaseEntity
{
    public string CheckName { get; set; } = string.Empty;
    public DateTime? CompletedAt { get; set; }
    public string? Notes { get; set; }
}
