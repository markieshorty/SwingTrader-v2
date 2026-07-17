using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IStrategyShareRepository
{
    Task<StrategyShare> AddAsync(StrategyShare share, CancellationToken ct = default);
    Task<StrategyShare?> GetByIdAsync(int accountId, int id, CancellationToken ct = default);

    // Shares RECEIVED by an account, newest first - the Shared Strategies page.
    Task<List<StrategyShare>> ListForRecipientAsync(int accountId, CancellationToken ct = default);

    // Shares SENT by an account, newest first - the admin tab's sent history.
    Task<List<StrategyShare>> ListForSenderAsync(int senderAccountId, CancellationToken ct = default);

    // Count of undismissed, unapplied shares - drives the nav badge without
    // pulling snapshot payloads.
    Task<int> CountPendingForRecipientAsync(int accountId, CancellationToken ct = default);

    // Total shares ever received - the nav item only renders when > 0.
    Task<int> CountAllForRecipientAsync(int accountId, CancellationToken ct = default);

    Task UpdateAsync(StrategyShare share, CancellationToken ct = default);
}
