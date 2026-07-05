using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AccountInviteRepository(SwingTraderDbContext db) : IAccountInviteRepository
{
    public async Task<AccountInvite> CreateAsync(AccountInvite invite, CancellationToken ct = default)
    {
        db.AccountInvites.Add(invite);
        await db.SaveChangesAsync(ct);
        return invite;
    }

    public Task<AccountInvite?> FindValidByTokenAsync(string token, CancellationToken ct = default) =>
        db.AccountInvites.FirstOrDefaultAsync(
            i => i.Token == token && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow, ct);

    public async Task MarkAcceptedAsync(int inviteId, string acceptedByUserId, CancellationToken ct = default)
    {
        var invite = await db.AccountInvites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null) return;
        invite.AcceptedAt = DateTime.UtcNow;
        invite.AcceptedByUserId = acceptedByUserId;
        await db.SaveChangesAsync(ct);
    }
}
