using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class BacktestRunRepository(SwingTraderDbContext db) : IBacktestRunRepository
{
    public async Task<BacktestRun> AddAsync(BacktestRun run)
    {
        db.BacktestRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public Task<BacktestRun?> GetByIdAsync(int accountId, int id) =>
        db.BacktestRuns.FirstOrDefaultAsync(r => r.AccountId == accountId && r.Id == id);

    public async Task UpdateAsync(BacktestRun run)
    {
        run.UpdatedAt = DateTime.UtcNow;
        db.BacktestRuns.Update(run);
        await db.SaveChangesAsync();
    }
}
