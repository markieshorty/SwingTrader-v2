using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface INotificationRecipientRepository
{
    Task<List<NotificationRecipient>> ListAsync(int accountId, CancellationToken ct = default);
    Task<NotificationRecipient> AddAsync(NotificationRecipient recipient, CancellationToken ct = default);
    Task RemoveAsync(int accountId, int recipientId, CancellationToken ct = default);
}
