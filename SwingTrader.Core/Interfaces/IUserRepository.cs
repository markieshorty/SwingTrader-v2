using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IUserRepository
{
    Task<AppUser?> FindAsync(string userId, CancellationToken ct = default);
    Task<AppUser> CreateAsync(AppUser user, CancellationToken ct = default);

    Task UpdateLastLoginAsync(string userId, CancellationToken ct = default);

    // Self-service - the onboarding wizard's email-confirmation step lets a
    // user directly correct their own Email, since it can't be reliably
    // trusted from the auth token for every identity provider/tenant setup.
    Task UpdateEmailAsync(string userId, string email, CancellationToken ct = default);
    Task<List<AppUser>> ListByAccountAsync(int accountId, CancellationToken ct = default);
    Task RemoveAsync(string userId, CancellationToken ct = default);
    Task ApproveAsync(string userId, CancellationToken ct = default);

    // Admin actions.
    Task SuspendAsync(string userId, string? reason, CancellationToken ct = default);
    Task UnsuspendAsync(string userId, CancellationToken ct = default);
    Task ResetOnboardingAsync(string userId, CancellationToken ct = default);
    Task MarkOnboardedAsync(string userId, CancellationToken ct = default);

    // The friends-and-family gate - superadmin-only, independent of the
    // per-account ApproveAsync above (which an Owner uses on their own Members).
    Task AdminApproveAsync(string userId, CancellationToken ct = default);
}
