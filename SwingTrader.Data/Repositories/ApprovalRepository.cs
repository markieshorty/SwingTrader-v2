using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class ApprovalRepository(SwingTraderDbContext context) : IApprovalRepository
{
    public Task<TradeApproval?> GetByDateAsync(int accountId, DateOnly date) =>
        context.TradeApprovals.FirstOrDefaultAsync(x => x.AccountId == accountId && x.TradeDate == date);

    public Task<TradeApproval?> GetByTokenAsync(string token) =>
        context.TradeApprovals.FirstOrDefaultAsync(x => x.ApprovalToken == token);

    public Task<TradeApproval?> GetByIdAsync(int accountId, int id) =>
        context.TradeApprovals.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public Task<List<TradeApproval>> ListRecentAsync(int accountId, int count) =>
        context.TradeApprovals
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.TradeDate)
            .ThenByDescending(x => x.CreatedAt)
            .Take(count)
            .ToListAsync();

    public async Task<TradeApproval> AddAsync(TradeApproval approval)
    {
        context.TradeApprovals.Add(approval);
        await context.SaveChangesAsync();
        return approval;
    }

    public async Task UpdateAsync(TradeApproval approval)
    {
        approval.UpdatedAt = DateTime.UtcNow;
        context.TradeApprovals.Update(approval);
        await context.SaveChangesAsync();
    }

    public Task<bool> AnyApprovedAsync(int accountId) =>
        context.TradeApprovals.AnyAsync(x => x.AccountId == accountId && x.IsApproved);
}
