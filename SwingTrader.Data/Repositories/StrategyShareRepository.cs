using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class StrategyShareRepository(SwingTraderDbContext db) : IStrategyShareRepository
{
    public async Task<StrategyShare> AddAsync(StrategyShare share, CancellationToken ct = default)
    {
        db.StrategyShares.Add(share);
        await db.SaveChangesAsync(ct);
        return share;
    }

    public Task<StrategyShare?> GetByIdAsync(int accountId, int id, CancellationToken ct = default) =>
        db.StrategyShares.FirstOrDefaultAsync(s => s.AccountId == accountId && s.Id == id, ct);

    public Task<List<StrategyShare>> ListForRecipientAsync(int accountId, CancellationToken ct = default) =>
        db.StrategyShares
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.SentAt)
            .ToListAsync(ct);

    public Task<List<StrategyShare>> ListForSenderAsync(int senderAccountId, CancellationToken ct = default) =>
        db.StrategyShares
            .Where(s => s.SenderAccountId == senderAccountId)
            .OrderByDescending(s => s.SentAt)
            .ToListAsync(ct);

    public Task<int> CountPendingForRecipientAsync(int accountId, CancellationToken ct = default) =>
        db.StrategyShares.CountAsync(s => s.AccountId == accountId && s.Status == "Sent", ct);

    public Task<int> CountAllForRecipientAsync(int accountId, CancellationToken ct = default) =>
        db.StrategyShares.CountAsync(s => s.AccountId == accountId, ct);

    public async Task UpdateAsync(StrategyShare share, CancellationToken ct = default)
    {
        share.UpdatedAt = DateTime.UtcNow;
        db.StrategyShares.Update(share);
        await db.SaveChangesAsync(ct);
    }
}
