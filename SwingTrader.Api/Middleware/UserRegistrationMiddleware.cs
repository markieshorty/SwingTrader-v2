using System.Collections.Concurrent;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Middleware;

// Handles three cases per request: brand-new login (no AppUser yet, no
// invite token - create a new Account, make them Owner), invite
// acceptance (no AppUser yet, valid invite token present - join the
// inviter's Account as Member), and returning user (AppUser exists -
// just refresh AccountId/Role into HttpContext.Items).
public class UserRegistrationMiddleware(RequestDelegate next)
{
    // The Angular app fires several /api/* requests in parallel on first
    // load (the dashboard's forkJoin, the onboarding guard's key check,
    // etc.), so a brand-new user's very first login sends multiple
    // concurrent requests through this middleware at once. Without a lock,
    // every one of them independently sees FindAsync(userId) return null
    // (no AppUser committed yet) and each creates its own orphan Account -
    // AppUsers.UserId has a unique index so only one AppUser insert wins,
    // but by then every racer has already committed its own Account row.
    // Confirmed in production: 442 orphan Accounts against a single
    // AppUser. A per-userId lock, not a single global one, so unrelated
    // users' first logins don't serialize behind each other. Only covers
    // a single Container App instance (currently minReplicas 0/maxReplicas
    // 1) - scaling out horizontally would need a DB-level fix (unique
    // constraint + retry, or a serializable transaction) instead.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RegistrationLocks = new();

    public async Task InvokeAsync(
        HttpContext context,
        IUserRepository users,
        IAccountRepository accounts,
        IAccountInviteRepository invites,
        IWatchlistRepository watchlists,
        IStrategyWeightsRepository weights)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst("sub")?.Value!;
            // CIAM emits a singular "email" claim (unlike classic B2C's
            // "emails" array) and only when the "email" scope is requested.
            var email = context.User.FindFirst("email")?.Value
                ?? context.User.FindFirst("emails")?.Value
                ?? context.User.FindFirst("preferred_username")?.Value
                ?? string.Empty;
            var user = await users.FindAsync(userId);

            if (user is null)
            {
                var registrationLock = RegistrationLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
                await registrationLock.WaitAsync();
                try
                {
                    // Re-check now that we hold the lock - a concurrent request
                    // may have already finished creating this user while we waited.
                    user = await users.FindAsync(userId);
                    user ??= await RegisterNewUserAsync(context, userId, email, users, accounts, invites, watchlists, weights);
                }
                finally
                {
                    registrationLock.Release();
                }
            }
            else
            {
                var account = await accounts.GetAsync(user.AccountId!.Value);
                if (account is null || account.IsDeleted)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await users.UpdateLastLoginAsync(userId);
            }

            // Set before the approval gate below - IAccountContext (used by
            // the exempted /api/account/approval-status endpoint itself)
            // needs AccountId/Role resolvable even for an unapproved user.
            context.Items["AccountId"] = user.AccountId!.Value.ToString();
            context.Items["AccountRole"] = user.Role.ToString();

            // Members who joined via an invite link start unapproved and
            // can't touch anything else on the app - not a UI-only gate,
            // enforced here so a direct API call can't route around it
            // either. GET /api/account/approval-status is the one exempt
            // path, so the frontend has something to poll for the "waiting
            // for approval" screen.
            if (!user.IsApproved && !context.Request.Path.StartsWithSegments("/api/account/approval-status"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    pendingApproval = true,
                    message = "Your account is awaiting approval from the account owner.",
                });
                return;
            }
        }

        await next(context);
    }

    private static async Task<AppUser> RegisterNewUserAsync(
        HttpContext context,
        string userId,
        string email,
        IUserRepository users,
        IAccountRepository accounts,
        IAccountInviteRepository invites,
        IWatchlistRepository watchlists,
        IStrategyWeightsRepository weights)
    {
        var inviteToken = context.Request.Headers["X-Invite-Token"].FirstOrDefault();

        AccountInvite? invite = inviteToken is null
            ? null
            : await invites.FindValidByTokenAsync(inviteToken);

        int accountId;
        AccountRole role;

        if (invite is not null)
        {
            accountId = invite.AccountId;
            role = AccountRole.Member;
            await invites.MarkAcceptedAsync(invite.Id, userId);
        }
        else
        {
            var account = await accounts.CreateAsync(new Account());
            accountId = account.Id;
            role = AccountRole.Owner;

            await watchlists.SeedDefaultAsync(accountId);
            await weights.SeedDefaultAsync(accountId);
        }

        var user = new AppUser
        {
            UserId = userId,
            Email = email,
            DisplayName = context.User.FindFirst("name")?.Value ?? email,
            AccountId = accountId,
            Role = role,
            FirstLoginAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            IsApproved = role == AccountRole.Owner,
        };

        await users.CreateAsync(user);
        return user;
    }
}
