namespace SwingTrader.Core.Models;

public class WorkerRunLog : BaseEntity
{
    public string WorkerName { get; set; } = "";
    public DateTime RanAt { get; set; }
    public string Result { get; set; } = "";
    public string? Message { get; set; }
}
