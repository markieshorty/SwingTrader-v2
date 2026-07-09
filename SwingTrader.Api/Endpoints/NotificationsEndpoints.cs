using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class NotificationsEndpoints
{
    public static RouteGroupBuilder MapNotificationsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/account/notifications", async (INotificationRecipientRepository recipients, IAccountContext ctx) =>
        {
            var list = await recipients.ListAsync(ctx.AccountId);
            // Categories serializes as a comma-separated flag-name string (the global
            // JsonStringEnumConverter's default [Flags] behaviour) - expose a plain
            // computed bool instead of making the client parse that string.
            return Results.Ok(list.Select(r => new
            {
                r.Id,
                r.Email,
                TradeApprovalEnabled = r.Categories.HasFlag(NotificationCategory.TradeApproval),
            }));
        });

        api.MapPost("/account/notifications", async (
            AddNotificationRecipientRequest req,
            INotificationRecipientRepository recipients,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest(new { message = "Email cannot be empty." });

            var created = await recipients.AddAsync(new NotificationRecipient
            {
                AccountId = ctx.AccountId,
                Email = req.Email,
                Categories = req.Categories,
            });
            return Results.Ok(created);
        });

        api.MapDelete("/account/notifications/{recipientId:int}", async (
            int recipientId,
            INotificationRecipientRepository recipients,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
            {
                var all = await recipients.ListAsync(ctx.AccountId);
                var target = all.FirstOrDefault(r => r.Id == recipientId);
                if (target is null || !target.Email.Equals(ctx.Email, StringComparison.OrdinalIgnoreCase))
                    return Results.Forbid();
            }

            await recipients.RemoveAsync(ctx.AccountId, recipientId);
            return Results.Ok();
        });

        api.MapPut("/account/notifications/{recipientId:int}/trade-approval", async (
            int recipientId,
            SetTradeApprovalRequest req,
            INotificationRecipientRepository recipients,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
            {
                var all = await recipients.ListAsync(ctx.AccountId);
                var target = all.FirstOrDefault(r => r.Id == recipientId);
                if (target is null || !target.Email.Equals(ctx.Email, StringComparison.OrdinalIgnoreCase))
                    return Results.Forbid();
            }

            var updated = await recipients.SetTradeApprovalAsync(ctx.AccountId, recipientId, req.Enabled);
            return updated ? Results.Ok() : Results.NotFound();
        });

        return api;
    }
}
