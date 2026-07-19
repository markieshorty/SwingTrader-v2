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

    // Projection keeps ResultJson (potentially hundreds of KB) out of the
    // poll; RequestJson is needed for the mode label so it rides along.
    // Stale cutoffs: a worker crash (e.g. the 18 Jul OOM) can leave a run
    // flagged "Running" forever with nothing processing it - without a
    // cutoff that ghost spins in the toolbar indefinitely. Functions'
    // functionTimeout is 3h, so anything Running longer than 4h (or Queued
    // longer than a day) is dead and drops out of the feed.
    public Task<List<BacktestRun>> GetActiveOrRecentAsync(int accountId, DateTime since, CancellationToken ct = default)
    {
        var runningCutoff = DateTime.UtcNow.AddHours(-4);
        var queuedCutoff = DateTime.UtcNow.AddHours(-24);
        return db.BacktestRuns
            .Where(r => r.AccountId == accountId
                && ((r.Status == "Queued" && r.CreatedAt >= queuedCutoff)
                    || (r.Status == "Running" && r.StartedAt != null && r.StartedAt >= runningCutoff)
                    || (r.CompletedAt != null && r.CompletedAt >= since)))
            .Select(r => new BacktestRun
            {
                Id = r.Id,
                AccountId = r.AccountId,
                Status = r.Status,
                RequestJson = r.RequestJson,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                TotalCandidates = r.TotalCandidates,
                CompletedCandidates = r.CompletedCandidates,
            })
            .ToListAsync(ct);
    }

    public Task<BacktestRun?> GetLatestCompletedByFingerprintAsync(
        int accountId, string mode, string fingerprint, CancellationToken ct = default) =>
        db.BacktestRuns
            .Where(r => r.AccountId == accountId
                && r.ConfigFingerprint == fingerprint
                && r.RequestJson.Contains($"\"Mode\":\"{mode}\"")
                && r.Status == "Completed" && r.ResultJson != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

    // Raw SQL because EF can't express JSON_QUERY; the whole point is that
    // ResultJson never leaves the server. Paths are camelCase to match the
    // consumer's serialization (SQL JSON paths are case-sensitive).
    public Task<List<BacktestHistorySlice>> GetHistorySlicesAsync(int accountId, string mode, int limit, CancellationToken ct = default)
    {
        var slicePath = mode == "sweep" ? "$.winner" : "$.candidates";
        return db.Database
            .SqlQuery<BacktestHistorySlice>($"""
                SELECT TOP ({limit}) Id, CompletedAt, RequestJson,
                       JSON_QUERY(ResultJson, {slicePath}) AS SliceJson
                FROM BacktestRuns
                WHERE AccountId = {accountId}
                  AND Status = 'Completed'
                  AND ResultJson IS NOT NULL
                  AND RequestJson LIKE '%' + {"\"Mode\":\"" + mode + "\""} + '%'
                ORDER BY CompletedAt DESC
                """)
            .ToListAsync(ct);
    }

    public Task<List<BacktestRun>> GetCompletedByModeAsync(int accountId, string mode, int limit, CancellationToken ct = default) =>
        db.BacktestRuns
            .Where(r => r.AccountId == accountId
                && r.RequestJson.Contains($"\"Mode\":\"{mode}\"")
                && r.Status == "Completed" && r.ResultJson != null)
            .OrderByDescending(r => r.CompletedAt)
            .Take(limit)
            .ToListAsync(ct);
}
