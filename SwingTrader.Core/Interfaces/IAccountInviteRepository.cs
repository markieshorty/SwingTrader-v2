using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IAccountInviteRepository
{
    Task<AccountInvite> CreateAsync(AccountInvite invite, CancellationToken ct = default);
    Task<AccountInvite?> FindValidByTokenAsync(string token, CancellationToken ct = default);
    Task MarkAcceptedAsync(int inviteId, string acceptedByUserId, CancellationToken ct = default);
}
