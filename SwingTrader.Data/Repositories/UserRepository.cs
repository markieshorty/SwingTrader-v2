using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class UserRepository(SwingTraderDbContext db) : IUserRepository
{
    public Task<AppUser?> FindAsync(string userId, CancellationToken ct = default) =>
        db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);

    public async Task<AppUser> CreateAsync(AppUser user, CancellationToken ct = default)
    {
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateLastLoginAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public Task<List<AppUser>> ListByAccountAsync(int accountId, CancellationToken ct = default) =>
        db.AppUsers.Where(u => u.AccountId == accountId).ToListAsync(ct);

    public async Task RemoveAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        db.AppUsers.Remove(user);
        await db.SaveChangesAsync(ct);
    }
}
