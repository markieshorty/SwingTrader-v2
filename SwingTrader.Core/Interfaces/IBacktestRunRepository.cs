using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IBacktestRunRepository
{
    Task<BacktestRun> AddAsync(BacktestRun run);
    Task<BacktestRun?> GetByIdAsync(int accountId, int id);
    Task UpdateAsync(BacktestRun run);
}
