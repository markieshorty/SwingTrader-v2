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

                user = new AppUser
                {
                    UserId = userId,
                    Email = email,
                    DisplayName = context.User.FindFirst("name")?.Value ?? email,
                    AccountId = accountId,
                    Role = role,
                    FirstLoginAt = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow,
                };

                await users.CreateAsync(user);
            }
            else
            {
                await users.UpdateLastLoginAsync(userId);
            }

            context.Items["AccountId"] = user.AccountId!.Value.ToString();
            context.Items["AccountRole"] = user.Role.ToString();
        }

        await next(context);
    }
}
