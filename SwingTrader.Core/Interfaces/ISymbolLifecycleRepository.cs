using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ISymbolLifecycleRepository
{
    Task AddAsync(SymbolLifecycle lifecycle, CancellationToken ct = default);
    Task<Dictionary<string, SymbolLifecycle>> GetAllAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<int> CountStoredAsync(CancellationToken ct = default);
}
