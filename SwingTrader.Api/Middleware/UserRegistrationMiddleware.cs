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
        IStrategyWeightsRepository weights,
        IAccountRiskProfileRepository riskProfiles,
        ISetupTacticsRepository setupTactics,
        INotificationRecipientRepository recipients)
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
            var displayName = context.User.FindFirst("name")?.Value ?? email;
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
                    user ??= await RegisterNewUserAsync(context, userId, email, displayName, users, accounts, invites, watchlists, weights, riskProfiles, setupTactics, recipients);
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

            // Set before the suspension/approval gates below - IAccountContext
            // (used by the exempted /api/account/approval-status endpoint
            // itself) needs AccountId/Role resolvable even for a gated user.
            context.Items["AccountId"] = user.AccountId!.Value.ToString();
            context.Items["AccountRole"] = user.Role.ToString();

            // Suspension blocks this specific person's API access - it does
            // NOT pause the Account's automated trading. Jobs are scheduled
            // per-Account (SchedulerFunction.ListActiveAsync), not per-user,
            // and a suspended Member shouldn't be able to halt the Owner's
            // trading (or vice versa) just by being suspended themselves.
            // Freezing a suspended Owner's own live trading, if ever needed,
            // is a separate deliberate action (e.g. switching TradingMode or
            // deactivating the Account), not an automatic side effect of
            // suspending the person's login.
            if (user.IsSuspended)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Account suspended",
                    message = "Contact support",
                });
                return;
            }

            // Friends-and-family gate: checked before the per-account
            // IsApproved gate below, and blocks EVERY unapproved person
            // regardless of role - an Owner approving their own Members
            // (the IsApproved gate) never grants this one, so there's no
            // "friend of a friend" path around the superadmin. Same exempt
            // paths as IsApproved below, so the frontend can still poll for
            // status AND let the person correct their email while blocked -
            // without that second exemption, the Unapproved admin tab would
            // only ever show the best-effort (possibly synthetic) email
            // captured at first login, making it impossible to recognize
            // who's actually asking for access.
            if (!user.AdminApproved && !IsApprovalGateExempt(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    pendingApproval = true,
                    adminApprovalRequired = true,
                    message = "This is an invite only system for Friends and Family - Wait for approval please. " +
                              "Note: If the owner does not recognize you, you will not be approved for entry.",
                });
                return;
            }

            // Members who joined via an invite link start unapproved and
            // can't touch anything else on the app - not a UI-only gate,
            // enforced here so a direct API call can't route around it
            // either.
            if (!user.IsApproved && !IsApprovalGateExempt(context))
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

    // Paths a gated (AdminApproved=false or IsApproved=false) user can still
    // reach: the status poll itself, and reading/correcting their own
    // email - the latter matters because the email captured at first login
    // can be a synthetic fallback for some identity providers, and the
    // superadmin approving them in the Unapproved tab needs the real one.
    private static bool IsApprovalGateExempt(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api/account/approval-status") ||
        context.Request.Path.StartsWithSegments("/api/account/me");

    private static async Task<AppUser> RegisterNewUserAsync(
        HttpContext context,
        string userId,
        string email,
        string displayName,
        IUserRepository users,
        IAccountRepository accounts,
        IAccountInviteRepository invites,
        IWatchlistRepository watchlists,
        IStrategyWeightsRepository weights,
        IAccountRiskProfileRepository riskProfiles,
        ISetupTacticsRepository setupTactics,
        INotificationRecipientRepository recipients)
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
            await riskProfiles.SeedDefaultAsync(accountId);
            await setupTactics.SeedDefaultAsync(accountId); // after risk profile — copies the Neutral book

            // Guarantees at least one person is subscribed to trade approval
            // emails the moment "Require approval" gets turned on - without
            // this, a brand-new account has zero recipients, so approval
            // requests would silently go nowhere until someone remembers to
            // add themselves in Settings. Email is best-effort at this point
            // (may still be a synthetic fallback for some identity
            // providers) but gets corrected in place once the user confirms
            // their real email in the onboarding wizard.
            await recipients.AddAsync(new NotificationRecipient
            {
                AccountId = accountId,
                Email = email,
                Categories = NotificationCategory.All,
            });
        }

        var user = new AppUser
        {
            UserId = userId,
            Email = email,
            DisplayName = displayName,
            AccountId = accountId,
            Role = role,
            FirstLoginAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            IsApproved = role == AccountRole.Owner,
            AdminApproved = false, // every new person, Owner or Member, waits on the superadmin regardless of role
        };

        await users.CreateAsync(user);
        return user;
    }
}
