namespace SwingTrader.Core.Interfaces;

public interface ISystemChecklistRepository
{
    Task CompleteAsync(int accountId, string checkName, string? notes = null);
}
