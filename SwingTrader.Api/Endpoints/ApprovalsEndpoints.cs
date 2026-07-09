using System.Text.Json;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class ApprovalsEndpoints
{
    public static RouteGroupBuilder MapApprovalsEndpoints(this RouteGroupBuilder api)
    {
        // In-app Approvals tab (Trades page) - the primary way to approve trades
        // now. The email is just a reminder pointing here rather than carrying an
        // actionable link, so this is authenticated like any other endpoint.
        api.MapGet("/approvals", async (IApprovalRepository approvals, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var rows = await approvals.ListRecentAsync(ctx.AccountId, account.TradingMode, 30);
            var result = rows.Select(a => new
            {
                a.Id,
                a.TradeDate,
                a.IsApproved,
                a.ApprovedAt,
                a.ApprovedSymbols,
                a.ApprovedVia,
                Candidates = a.CandidatesJson is null
                    ? []
                    : JsonSerializer.Deserialize<JsonElement[]>(a.CandidatesJson) ?? [],
            });
            return Results.Ok(result);
        });

        api.MapPost("/approvals/{id:int}/approve", async (
            int id,
            ApproveTradeApprovalRequest req,
            IApprovalRepository approvals,
            IJobLogRepository jobLog,
            IActivityLogRepository activityLog,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var approval = await approvals.GetByIdAsync(ctx.AccountId, id);
            if (approval is null) return Results.NotFound();
            if (approval.IsApproved)
                return Results.BadRequest(new { message = "Already approved." });
            var todayEt = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York")));
            if (approval.TradeDate < todayEt)
                return Results.BadRequest(new { message = "This approval has expired — signals are stale. New signals will be generated today." });

            approval.IsApproved = true;
            approval.ApprovedAt = DateTime.UtcNow;
            approval.ApprovedSymbols = string.IsNullOrWhiteSpace(req.Symbols) ? null : req.Symbols.Trim();
            approval.ApprovedVia = "app";
            await approvals.UpdateAsync(approval);

            await jobLog.DeleteAsync(ctx.AccountId, "Execution", approval.TradeDate);
            await activityLog.LogAsync(ctx.AccountId, "UserAction", "Trade Approved", "Info",
                BuildApprovalActivityMessage(approval, "app"));

            return Results.Ok();
        });

        // The public email-link /approve endpoint was removed (security): it was an
        // unauthenticated, never-expiring, single-token action that placed real-money
        // trades, with an attacker-controllable `symbols` query parameter. Approval
        // now happens exclusively through the authenticated in-app POST
        // /api/approvals/{id}/approve above; the approval email is a plain reminder
        // pointing at that Trades > Approvals tab, carrying no actionable link.

        return api;
    }

    static string BuildApprovalActivityMessage(TradeApproval approval, string via)
    {
        var symbols = new List<string>();
        if (!string.IsNullOrWhiteSpace(approval.CandidatesJson))
        {
            try
            {
                var candidates = JsonSerializer.Deserialize<JsonElement[]>(approval.CandidatesJson);
                if (candidates is not null)
                    symbols = candidates
                        .Where(c => c.TryGetProperty("symbol", out _))
                        .Select(c => c.GetProperty("symbol").GetString() ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
            }
            catch { /* ignore deserialization errors */ }
        }

        var symbolPart = symbols.Count > 0 ? $" — candidates: {string.Join(", ", symbols)}" : "";
        return $"Approved for {approval.TradeDate:dd MMM yyyy} via {via}{symbolPart}";
    }
}
