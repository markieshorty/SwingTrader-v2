using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class NotificationRecipientRepository(SwingTraderDbContext db) : INotificationRecipientRepository
{
    public Task<List<NotificationRecipient>> ListAsync(int accountId, CancellationToken ct = default) =>
        db.NotificationRecipients.Where(r => r.AccountId == accountId).ToListAsync(ct);

    public async Task<NotificationRecipient> AddAsync(NotificationRecipient recipient, CancellationToken ct = default)
    {
        db.NotificationRecipients.Add(recipient);
        await db.SaveChangesAsync(ct);
        return recipient;
    }

    public async Task RemoveAsync(int accountId, int recipientId, CancellationToken ct = default)
    {
        var existing = await db.NotificationRecipients
            .FirstOrDefaultAsync(r => r.AccountId == accountId && r.Id == recipientId, ct);
        if (existing is null) return;

        db.NotificationRecipients.Remove(existing);
        await db.SaveChangesAsync(ct);
    }
}
