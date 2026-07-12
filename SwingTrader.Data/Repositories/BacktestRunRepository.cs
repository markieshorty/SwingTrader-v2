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
    //
    // An in-flight (Queued/Running) run wins over any completed one: the UI
    // uses this on tab load to REATTACH to a sweep that survived a page
    // refresh - reattaching matters more than re-showing an old result,
    // which comes back on the next load once the run completes anyway.
    public async Task<BacktestRun?> GetLatestByModeAsync(int accountId, string mode, CancellationToken ct = default)
    {
        var byMode = db.BacktestRuns
            .Where(r => r.AccountId == accountId && r.RequestJson.Contains($"\"Mode\":\"{mode}\""));

        var active = await byMode
            .Where(r => r.Status == "Queued" || r.Status == "Running")
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (active is not null) return active;

        return await byMode
            .Where(r => r.Status == "Completed" && r.ResultJson != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);
    }
}
