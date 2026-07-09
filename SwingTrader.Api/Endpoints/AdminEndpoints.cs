using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin area — global across all accounts, gated by Admin:UserId (a single
        // B2C object ID), a separate concept from AccountRole.Owner. SendMessage is
        // deliberately not implemented in this pass (no in-app messaging channel
        // exists yet - deferred rather than half-built as an endpoint with nowhere
        // to deliver to).
        var adminGroup = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        adminGroup.MapGet("/me", () => Results.Ok(new { isAdmin = true }));

        adminGroup.MapGet("/stats", async (IAdminRepository admin, IUserKeyService keys, CancellationToken ct) =>
        {
            var stats = await admin.GetStatsAsync(ct);
            var users = await admin.GetUsersAsync(ct);

            var notOnboarded = 0;
            foreach (var user in users)
                if (!OnboardingStatus.IsReallyOnboarded(await keys.GetKeyStatusesAsync(user.AccountId, ct)))
                    notOnboarded++;

            return Results.Ok(stats with { UsersNotOnboarded = notOnboarded });
        });

        adminGroup.MapGet("/users", async (IAdminRepository admin, IUserKeyService keys, CancellationToken ct) =>
        {
            var users = await admin.GetUsersAsync(ct);
            var withRealOnboarding = new List<AdminUserSummary>(users.Count);
            foreach (var user in users)
                withRealOnboarding.Add(user with { IsOnboarded = OnboardingStatus.IsReallyOnboarded(await keys.GetKeyStatusesAsync(user.AccountId, ct)) });

            return Results.Ok(withRealOnboarding);
        });

        adminGroup.MapGet("/users/{userId}", async (string userId, IAdminRepository admin, IUserKeyService keys, CancellationToken ct) =>
        {
            var user = await admin.GetUserAsync(userId, ct);
            if (user is null) return Results.NotFound();

            var isOnboarded = OnboardingStatus.IsReallyOnboarded(await keys.GetKeyStatusesAsync(user.AccountId, ct));
            return Results.Ok(user with { IsOnboarded = isOnboarded });
        });

        adminGroup.MapPost("/users/{userId}/suspend", async (
            string userId,
            SuspendUserRequest req,
            IUserRepository users,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            await users.SuspendAsync(userId, req.Reason, ct);
            await adminLog.LogAsync(new AdminActionLog
            {
                AdminUserId = AdminId(http),
                TargetUserId = userId,
                Action = "Suspend",
                Details = req.Reason is null ? null : $"Reason: {req.Reason}",
            }, ct);
            return Results.Ok();
        });

        adminGroup.MapPost("/users/{userId}/unsuspend", async (
            string userId,
            IUserRepository users,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            await users.UnsuspendAsync(userId, ct);
            await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "Unsuspend" }, ct);
            return Results.Ok();
        });

        adminGroup.MapPost("/users/{userId}/reset-onboarding", async (
            string userId,
            IUserRepository users,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            await users.ResetOnboardingAsync(userId, ct);
            await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "ResetOnboarding" }, ct);
            return Results.Ok();
        });

        adminGroup.MapPost("/users/{userId}/force-demo", async (
            string userId,
            IUserRepository users,
            IAccountRepository accounts,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            var user = await users.FindAsync(userId, ct);
            if (user?.AccountId is null) return Results.NotFound();

            var account = await accounts.GetAsync(user.AccountId.Value);
            if (account is null) return Results.NotFound();

            account.TradingMode = TradingMode.Demo;
            await accounts.UpdateAsync(account, ct);
            await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "ForceDemo" }, ct);
            return Results.Ok();
        });

        adminGroup.MapDelete("/users/{userId}", async (
            string userId,
            IUserRepository users,
            IAccountRepository accounts,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            var user = await users.FindAsync(userId, ct);
            if (user is null) return Results.NotFound();

            if (user.Role == AccountRole.Owner && user.AccountId is not null)
            {
                // Soft-delete the whole Account, matching the self-service delete
                // path - an Owner's Account is theirs, not just their AppUser row.
                // Also remove every AppUser tied to it (Owner and any Members) -
                // otherwise their UserId row survives with AccountId pointing at a
                // now-deleted Account, and UserRegistrationMiddleware permanently
                // 403s them on every future login instead of letting them
                // re-register fresh, since it only creates a new Account when no
                // AppUser row exists yet for that identity.
                var account = await accounts.GetAsync(user.AccountId.Value);
                if (account is not null)
                {
                    account.IsDeleted = true;
                    await accounts.UpdateAsync(account, ct);
                }

                foreach (var member in await users.ListByAccountAsync(user.AccountId.Value, ct))
                    await users.RemoveAsync(member.UserId, ct);
            }
            else
            {
                await users.RemoveAsync(userId, ct);
            }

            await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "DeleteUser" }, ct);
            return Results.Ok();
        });

        adminGroup.MapGet("/jobs/failures", async (IAdminRepository admin, CancellationToken ct) =>
            Results.Ok(await admin.GetJobFailuresAsync(TimeSpan.FromHours(48), ct)));

        adminGroup.MapPost("/jobs/retry", async (
            RetryJobRequest req,
            IAdminRepository admin,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            var retried = await admin.RetryJobAsync(req.JobLogId, ct);
            if (!retried) return Results.NotFound(new { message = "Job not found or not in a failed state." });

            await adminLog.LogAsync(new AdminActionLog
            {
                AdminUserId = AdminId(http),
                TargetUserId = "system",
                Action = "RetryJob",
                Details = $"JobLogId: {req.JobLogId}",
            }, ct);
            return Results.Ok();
        });

        adminGroup.MapDelete("/jobs/{jobLogId:int}", async (
            int jobLogId,
            IAdminRepository admin,
            IAdminLogRepository adminLog,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await admin.DeleteJobFailureAsync(jobLogId, ct);
            if (!deleted) return Results.NotFound(new { message = "Job not found or not in a failed state." });

            await adminLog.LogAsync(new AdminActionLog
            {
                AdminUserId = AdminId(http),
                TargetUserId = "system",
                Action = "DeleteJobFailure",
                Details = $"JobLogId: {jobLogId}",
            }, ct);
            return Results.Ok();
        });

        adminGroup.MapGet("/logs", async (IAdminLogRepository adminLog, CancellationToken ct) =>
            Results.Ok(await adminLog.GetRecentAsync(200, ct)));

        return app;
    }

    static string AdminId(HttpContext context) => context.User.FindFirst("sub")?.Value ?? "unknown";
}
