using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface INotificationRecipientRepository
{
    Task<List<NotificationRecipient>> ListAsync(int accountId, CancellationToken ct = default);
    Task<NotificationRecipient> AddAsync(NotificationRecipient recipient, CancellationToken ct = default);
    Task RemoveAsync(int accountId, int recipientId, CancellationToken ct = default);

    // A dedicated toggle rather than a generic "set categories to X" update -
    // Categories serializes as a comma-separated flag-name string (the global
    // JsonStringEnumConverter's default [Flags] behaviour), so having the
    // client reconstruct a bitmask to send back would be fragile. This is
    // the only category the UI lets a user toggle per-recipient today.
    Task<bool> SetTradeApprovalAsync(int accountId, int recipientId, bool enabled, CancellationToken ct = default);

    // Keeps the auto-seeded owner recipient row (created at account
    // registration, before the email-confirmation step) in sync once the
    // user confirms their real email - a no-op if oldEmail no longer
    // matches any recipient (e.g. it was already changed manually).
    Task UpdateEmailIfMatchesAsync(int accountId, string oldEmail, string newEmail, CancellationToken ct = default);
}
