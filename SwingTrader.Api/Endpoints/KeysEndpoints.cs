using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class KeysEndpoints
{
    public static RouteGroupBuilder MapKeysEndpoints(this RouteGroupBuilder api)
    {
        // Per-account API key storage (Phase 10d). GetKeyStatuses never returns the
        // actual key values - only status - since these are third-party trading/
        // data credentials.
        api.MapGet("/keys", async (IUserKeyService keys, IAccountContext ctx) =>
            Results.Ok(await keys.GetKeyStatusesAsync(ctx.AccountId)));

        api.MapPost("/keys/{provider}", async (
            string provider,
            SaveKeyRequest req,
            IUserKeyService keys,
            IUserRepository users,
            [FromServices] ServiceBusClient? serviceBus,
            IAccountContext ctx,
            // test=false saves without a connectivity check - used when the caller
            // will test separately (e.g. saving both halves of a Trading212 pair then
            // hitting Connect once), so we don't fire redundant back-to-back T212
            // calls and trip its rate limit.
            bool test = true) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            if (!ApiKeyProviders.All.Contains(provider))
                return Results.BadRequest(new { message = $"Unknown provider '{provider}'." });
            if (string.IsNullOrWhiteSpace(req.Value))
                return Results.BadRequest(new { message = "Value cannot be empty." });

            await keys.SaveKeyAsync(ctx.AccountId, provider, req.Value);
            var testResult = test ? await keys.TestKeyAsync(ctx.AccountId, provider) : new KeyTestResult(false, "Saved (not tested)");

            // The moment a user's keys satisfy onboarding for the first time, kick
            // off an immediate Watchlist run rather than making them wait until the
            // next Sunday 20:00 ET schedule - IsOnboarded is otherwise unused, so it
            // doubles as the "have we already fired this" guard.
            var user = await users.FindAsync(ctx.UserId);
            if (user is { IsOnboarded: false } && serviceBus is not null)
            {
                var statuses = await keys.GetKeyStatusesAsync(ctx.AccountId);
                if (OnboardingStatus.IsReallyOnboarded(statuses))
                {
                    await users.MarkOnboardedAsync(ctx.UserId);
                    var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York"));
                    await using var sender = serviceBus.CreateSender("watchlist-jobs");
                    await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                        new WatchlistJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), nowEt))));
                }
            }

            return Results.Ok(new
            {
                valid = testResult.Valid,
                message = testResult.Message,
                isDemo = testResult.IsDemo,
                cashTotal = testResult.CashTotal,
                cashFree = testResult.CashFree,
                currency = testResult.Currency,
            });
        });

        api.MapGet("/keys/{provider}/test", async (
            string provider,
            IUserKeyService keys,
            IAccountContext ctx) =>
        {
            var result = await keys.TestKeyAsync(ctx.AccountId, provider);
            return Results.Ok(new
            {
                valid = result.Valid,
                message = result.Message,
                isDemo = result.IsDemo,
                cashTotal = result.CashTotal,
                cashFree = result.CashFree,
                currency = result.Currency,
            });
        });

        // Test a whole Trading212 pair (key + secret) for one mode - the "Connect to
        // demo/live" buttons. Verifies against that mode's endpoint regardless of the
        // account's current TradingMode and returns the balance + environment.
        api.MapGet("/keys/trading212/{mode}/test", async (
            string mode,
            IUserKeyService keys,
            IAccountContext ctx) =>
        {
            if (!Enum.TryParse<TradingMode>(mode, ignoreCase: true, out var tradingMode))
                return Results.BadRequest(new { message = $"Unknown mode '{mode}'." });

            var result = await keys.TestTrading212PairAsync(ctx.AccountId, tradingMode);
            return Results.Ok(new
            {
                valid = result.Valid,
                message = result.Message,
                isDemo = result.IsDemo,
                cashTotal = result.CashTotal,
                cashFree = result.CashFree,
                currency = result.Currency,
            });
        });

        api.MapDelete("/keys/{provider}", async (
            string provider,
            IUserKeyService keys,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            await keys.DeleteKeyAsync(ctx.AccountId, provider);
            return Results.Ok();
        });

        return api;
    }
}
