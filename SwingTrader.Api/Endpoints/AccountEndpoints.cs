using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder api)
    {
        // Account/invite management (Owner-only for mutating operations)
        api.MapPost("/account/invites", async (
            InviteRequest req,
            IAccountInviteRepository invites,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var invite = new SwingTrader.Core.Models.AccountInvite
            {
                AccountId = ctx.AccountId,
                InvitedByUserId = ctx.UserId,
                InvitedEmail = req.Email,
                Token = Guid.NewGuid().ToString("N"),
                // Short-lived deliberately: this token is the entire authentication
                // for joining someone's account. A 7-day window meant a leaked/
                // forwarded link (chat scrollback, shared clipboard, proxy logs)
                // stayed exploitable for a week; 30 minutes still comfortably covers
                // "share the link, they click it" while shrinking that exposure a lot.
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            };
            await invites.CreateAsync(invite);

            // Returned to the owner to share directly - no email is sent automatically.
            return Results.Ok(new { inviteUrl = $"{req.AppBaseUrl}/join?invite={invite.Token}" });
        });

        api.MapGet("/account/members", async (IUserRepository users, IAccountContext ctx) =>
            Results.Ok(await users.ListByAccountAsync(ctx.AccountId)));

        api.MapGet("/account", async (IAccountRepository accounts, IAccountContext ctx) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId)
                ?? throw new InvalidOperationException("Authenticated caller has no account.");
            return Results.Ok(new
            {
                account.TradingMode,
                account.ApprovalRequired,
                account.T212AccountId,
                account.GlobalRefinementOptIn,
                // Pause state for the mode currently in effect - the Settings
                // toggle and dashboard capsule are scoped to whichever mode
                // the account is on. Reason/PausedAt are only meaningful while
                // paused (Manual switch vs circuit-breaker auto-pause).
                ExecutionPaused = account.IsExecutionPaused(account.TradingMode),
                ExecutionPauseReason = account.ExecutionPauseReasonFor(account.TradingMode).ToString(),
                ExecutionPausedAt = account.ExecutionPausedAtFor(account.TradingMode),
                role = ctx.Role,
            });
        });

        api.MapGet("/account/me", async (IUserRepository users, IAccountContext ctx) =>
        {
            var user = await users.FindAsync(ctx.UserId);
            return Results.Ok(new
            {
                hasConfirmedEmail = user?.HasConfirmedEmail ?? false,
                email = user?.Email ?? string.Empty,
                displayName = user?.DisplayName ?? string.Empty,
            });
        });

        api.MapPut("/account/me/email", async (
            UpdateMyEmailRequest req,
            IUserRepository users,
            INotificationRecipientRepository recipients,
            IAccountContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
                return Results.BadRequest(new { message = "Enter a valid email address." });

            var newEmail = req.Email.Trim();
            var user = await users.FindAsync(ctx.UserId);
            var oldEmail = user?.Email;

            await users.UpdateEmailAsync(ctx.UserId, newEmail);

            // Keeps the auto-seeded notification recipient (created at registration
            // with a best-effort, possibly-synthetic email) in sync with the real,
            // user-confirmed one - a no-op if it was never seeded or already differs.
            if (!string.IsNullOrWhiteSpace(oldEmail))
                await recipients.UpdateEmailIfMatchesAsync(ctx.AccountId, oldEmail, newEmail);

            // Only Owners should be auto-seeded into the recipients list — Members
            // joining via invite should never appear there.
            if (ctx.Role == AccountRole.Owner)
            {
                var existingRecipients = await recipients.ListAsync(ctx.AccountId);
                if (!existingRecipients.Any(r => r.Email.Equals(newEmail, StringComparison.OrdinalIgnoreCase)))
                {
                    await recipients.AddAsync(new NotificationRecipient
                    {
                        AccountId = ctx.AccountId,
                        Email = newEmail,
                        Categories = NotificationCategory.All,
                    });
                }
            }

            return Results.Ok();
        });

        api.MapDelete("/account/members/{userId}", async (
            string userId,
            IUserRepository users,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            if (userId == ctx.UserId)
            {
                var members = await users.ListByAccountAsync(ctx.AccountId);
                if (members.Count(m => m.Role == AccountRole.Owner) <= 1)
                    return Results.BadRequest(new { message = "Cannot remove the sole Owner from an account." });
            }

            await users.RemoveAsync(userId);
            return Results.Ok();
        });

        api.MapPut("/account/members/{userId}/approve", async (
            string userId,
            IUserRepository users,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var members = await users.ListByAccountAsync(ctx.AccountId);
            if (!members.Any(m => m.UserId == userId))
                return Results.NotFound();

            await users.ApproveAsync(userId);
            return Results.Ok();
        });

        // The one path UserRegistrationMiddleware exempts from the pending-approval
        // block, so an unapproved user's "waiting for approval" screen has
        // something to poll without a 403 loop.
        api.MapGet("/account/approval-status", async (IUserRepository users, IAccountContext ctx) =>
        {
            var user = await users.FindAsync(ctx.UserId);
            return Results.Ok(new { isApproved = user?.IsApproved ?? false });
        });

        // Trading config, notifications, and account lifecycle (Phase 10d Settings page)
        api.MapPut("/account/trading-config", async (
            UpdateTradingConfigRequest req,
            IAccountRepository accounts,
            ITradeRepository trades,
            IUserKeyService keys,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var account = await accounts.GetAsync(ctx.AccountId)
                ?? throw new InvalidOperationException("Authenticated caller has no account.");

            // Trading212 issues separate credentials per environment - switching
            // TradingMode without the matching pair saved would just break every
            // T212 call the account makes, so block the switch until that pair exists.
            if (req.TradingMode != account.TradingMode)
            {
                var (keyProvider, secretProvider) = req.TradingMode == TradingMode.Live
                    ? (ApiKeyProviders.Trading212LiveKey, ApiKeyProviders.Trading212LiveSecret)
                    : (ApiKeyProviders.Trading212DemoKey, ApiKeyProviders.Trading212DemoSecret);

                var statuses = await keys.GetKeyStatusesAsync(ctx.AccountId);
                var hasPair = statuses.GetValueOrDefault(keyProvider) != KeyStatus.NotSet
                    && statuses.GetValueOrDefault(secretProvider) != KeyStatus.NotSet;

                if (!hasPair)
                    return Results.BadRequest(new
                    {
                        message = $"Add your Trading212 {req.TradingMode} API key and secret in Settings before switching to {req.TradingMode} mode.",
                    });

                // Monitor only watches trades matching the account's *current* mode,
                // so switching away with positions still open would silently orphan
                // them - no stop-loss, target, trailing-stop, or time-exit
                // enforcement until switching back. Not a hard block: the frontend
                // surfaces a confirm dialog (canForce) so the user acknowledges the
                // risk, then re-submits with Force=true. Especially important going
                // Live -> Demo, where settling real positions first is unreasonable.
                var openInCurrentMode = (await trades.GetOpenTradesAsync(ctx.AccountId, account.TradingMode)).ToList();
                if (openInCurrentMode.Count > 0 && !req.Force)
                    return Results.BadRequest(new
                    {
                        message = $"You have {openInCurrentMode.Count} open {account.TradingMode} position(s) " +
                            $"({string.Join(", ", openInCurrentMode.Select(t => t.Symbol))}). " +
                            $"Acme Trading stops monitoring them the moment you switch modes.",
                        canForce = true,
                    });
            }

            account.TradingMode = req.TradingMode;
            account.ApprovalRequired = req.ApprovalRequired;
            await accounts.UpdateAsync(account);
            return Results.Ok();
        });

        // Pause / resume new-position executions for the account's current
        // mode. Applied immediately (its own toggle, not the Trading "Save"
        // button) so a user watching a bad market can stop new buys in one
        // click. Scoped to account.TradingMode so Demo and Live pause
        // independently. Monitor is unaffected - open positions keep their
        // stop-loss/target/time exits while paused.
        api.MapPut("/account/execution-paused/{paused:bool}", async (
            bool paused,
            IAccountRepository accounts,
            IActivityLogRepository activityLog,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            var account = await accounts.GetAsync(ctx.AccountId)
                ?? throw new InvalidOperationException("Authenticated caller has no account.");

            if (paused)
                account.PauseExecution(account.TradingMode, ExecutionPauseReason.Manual, DateTime.UtcNow);
            else
                account.ResumeExecution(account.TradingMode);

            await accounts.UpdateAsync(account);

            // Audit trail so the account's activity feed shows who stopped/
            // started entries and when (the circuit-breaker auto-pause logs
            // its own separate entry from Monitor).
            await activityLog.LogAsync(ctx.AccountId, "UserAction",
                paused ? "Entries Paused" : "Entries Resumed", "Info",
                $"{account.TradingMode} entries {(paused ? "paused" : "resumed")} manually");

            return Results.Ok();
        });

        api.MapPut("/account/global-refinement-optin/{enabled:bool}", async (
            bool enabled,
            IAccountRepository accounts,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            var account = await accounts.GetAsync(ctx.AccountId)
                ?? throw new InvalidOperationException("Authenticated caller has no account.");
            account.GlobalRefinementOptIn = enabled;
            await accounts.UpdateAsync(account);
            return Results.Ok();
        });

        // Soft-delete only - see Account.IsDeleted for why a hard delete isn't
        // feasible without cascading through every scoped table.
        api.MapDelete("/account", async (IAccountRepository accounts, IUserRepository users, IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var account = await accounts.GetAsync(ctx.AccountId)
                ?? throw new InvalidOperationException("Authenticated caller has no account.");
            account.IsDeleted = true;
            await accounts.UpdateAsync(account);

            // Also remove every AppUser tied to this Account (Owner and any
            // Members) - otherwise their UserId row survives with AccountId
            // pointing at a now-deleted Account, and UserRegistrationMiddleware
            // permanently 403s them on every future login instead of letting them
            // re-register fresh.
            foreach (var member in await users.ListByAccountAsync(ctx.AccountId))
                await users.RemoveAsync(member.UserId);

            return Results.Ok();
        });

        return api;
    }
}
