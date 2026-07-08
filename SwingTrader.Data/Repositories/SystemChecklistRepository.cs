using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class SystemChecklistRepository(SwingTraderDbContext context) : ISystemChecklistRepository
{
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
}
