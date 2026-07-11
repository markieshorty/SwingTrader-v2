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

    // Mode lives inside RequestJson (serialized HistoricBacktestRequest with
    // PascalCase properties), not as a column - a LIKE on the exact
    // "Mode":"..." fragment avoids a schema change for what is a rare,
    // tab-load-only query over a small per-account table.
    public Task<BacktestRun?> GetLatestCompletedByModeAsync(int accountId, string mode, CancellationToken ct = default) =>
        db.BacktestRuns
            .Where(r => r.AccountId == accountId
                        && r.Status == "Completed"
                        && r.ResultJson != null
                        && r.RequestJson.Contains($"\"Mode\":\"{mode}\""))
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);
}
