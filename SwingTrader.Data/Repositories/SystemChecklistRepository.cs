using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class SystemChecklistRepository(SwingTraderDbContext context) : ISystemChecklistRepository
{
    public Task<SystemChecklist?> GetAsync(int accountId, string checkName) =>
        context.SystemChecklists.FirstOrDefaultAsync(x => x.AccountId == accountId && x.CheckName == checkName);

    public async Task<IEnumerable<SystemChecklist>> GetAllAsync(int accountId) =>
        await context.SystemChecklists
            .Where(x => x.AccountId == accountId)
            .OrderBy(x => x.CheckName)
            .ToListAsync();

    public async Task CompleteAsync(int accountId, string checkName, string? notes = null)
    {
        var existing = await context.SystemChecklists
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.CheckName == checkName);
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            context.SystemChecklists.Add(new SystemChecklist
            {
                AccountId = accountId,
                CheckName = checkName,
                CompletedAt = now,
                Notes = notes,
                CreatedAt = now,
            });
        }
        else if (existing.CompletedAt is null)
        {
            // Idempotent — calling twice keeps the original completion timestamp.
            existing.CompletedAt = now;
            existing.Notes = notes;
            existing.UpdatedAt = now;
        }
        await context.SaveChangesAsync();
    }

    public async Task<bool> IsCompletedAsync(int accountId, string checkName)
    {
        var item = await GetAsync(accountId, checkName);
        return item?.CompletedAt is not null;
    }
}
