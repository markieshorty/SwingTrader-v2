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

    public async Task ApproveAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.IsApproved = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task SuspendAsync(string userId, string? reason, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.IsSuspended = true;
        user.SuspendedAt = DateTime.UtcNow;
        user.SuspendReason = reason;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsuspendAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.IsSuspended = false;
        user.SuspendedAt = null;
        user.SuspendReason = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetOnboardingAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.IsOnboarded = false;
        user.OnboardingStep = 0;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkOnboardedAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.IsOnboarded = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateEmailAsync(string userId, string email, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null) return;
        user.Email = email;
        user.HasConfirmedEmail = true;
        await db.SaveChangesAsync(ct);
    }
}
