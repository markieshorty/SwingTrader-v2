using System.ComponentModel.DataAnnotations;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public record UpdateAllocationRequest(
    decimal SpyCorePct,     // fractions, must sum to 1 with the others
    decimal FactorTiltPct,
    decimal SwingPct,
    string CoreTicker);

// Capital sleeves (docs/sleeves-plan P1): the per-account allocation pie.
public static class AllocationEndpoints
{
    public static RouteGroupBuilder MapAllocationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/allocation", async (
            IAccountAllocationRepository allocations, IAccountContext ctx, CancellationToken ct) =>
        {
            var a = await allocations.GetAsync(ctx.AccountId, ct);
            return Results.Ok(new { a.SpyCorePct, a.FactorTiltPct, a.SwingPct, a.CoreTicker });
        });

        api.MapPut("/allocation", async (
            UpdateAllocationRequest req,
            IAccountAllocationRepository allocations,
            IActivityLogRepository activityLog,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            try
            {
                var saved = await allocations.UpsertAsync(new AccountAllocation
                {
                    AccountId = ctx.AccountId,
                    SpyCorePct = req.SpyCorePct,
                    FactorTiltPct = req.FactorTiltPct,
                    SwingPct = req.SwingPct,
                    CoreTicker = req.CoreTicker.Trim().ToUpperInvariant(),
                }, ct);
                await activityLog.LogAsync(ctx.AccountId, "SystemEvent", "Sleeve Allocation Changed", "Info",
                    $"Capital pie now: SPY core {saved.SpyCorePct:P0} ({saved.CoreTicker}), factor {saved.FactorTiltPct:P0}, swing {saved.SwingPct:P0}. " +
                    "Swing sizing uses the new slice from the next execution; the core rebalances on the next monitor cycle (5% band).", ct);
                return Results.Ok(new { saved.SpyCorePct, saved.FactorTiltPct, saved.SwingPct, saved.CoreTicker });
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        return api;
    }
}
