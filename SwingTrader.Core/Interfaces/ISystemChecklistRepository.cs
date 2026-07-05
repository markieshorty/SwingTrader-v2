using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ISystemChecklistRepository
{
    Task<SystemChecklist?> GetAsync(int accountId, string checkName);
    Task<IEnumerable<SystemChecklist>> GetAllAsync(int accountId);
    Task CompleteAsync(int accountId, string checkName, string? notes = null);
    Task<bool> IsCompletedAsync(int accountId, string checkName);
}
