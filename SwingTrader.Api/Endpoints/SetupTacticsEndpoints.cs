using System.ComponentModel.DataAnnotations;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class SetupTacticsEndpoints
{
    // Per-setup entry/exit tactics (docs/setup-tactics-plan): the stop, target,
    // guide-hold and trailing applied to a trade based on the setup that
    // triggered it. The regime risk book stays the exposure envelope.
    public static RouteGroupBuilder MapSetupTacticsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/setup-tactics", async (ISetupTacticsRepository repo, IAccountContext ctx) =>
        {
            var all = await repo.GetAllAsync(ctx.AccountId);
            return Results.Ok(new
            {
                Setups = all.Select(t => new
                {
                    SetupType = t.SetupType.ToString(),
                    t.Enabled,
                    t.StopLossPct,
                    t.TargetPct,
                    t.GuideHoldDays,
                    t.TrailingActivationPct,
                    t.TrailingDistancePct,
                }),
                AllowedRanges = new
                {
                    StopLossPct = new { Min = CapitalRules.MinStopLossPct, Max = CapitalRules.MaxStopLossPct },
                    TargetPct = new { Min = CapitalRules.MinTargetPct, Max = CapitalRules.MaxTargetPct },
                    GuideHoldDays = new { Min = CapitalRules.MinMaxHoldDays, Max = CapitalRules.MaxMaxHoldDays },
                    TrailingActivationPct = new { Min = CapitalRules.MinTrailingActivationPct, Max = CapitalRules.MaxTrailingActivationPct },
                    TrailingDistancePct = new { Min = CapitalRules.MinTrailingDistancePct, Max = CapitalRules.MaxTrailingDistancePct },
                },
            });
        });

        api.MapPut("/setup-tactics", async (
            UpdateSetupTacticsRequest req,
            ISetupTacticsRepository repo,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            if (!Enum.TryParse<SetupType>(req.SetupType, ignoreCase: true, out var setup))
                return Results.BadRequest(new { message = $"Unknown setup '{req.SetupType}'." });

            try
            {
                await repo.UpdateAsync(new SetupTactics
                {
                    AccountId = ctx.AccountId,
                    SetupType = setup,
                    Enabled = req.Enabled,
                    StopLossPct = req.StopLossPct,
                    TargetPct = req.TargetPct,
                    GuideHoldDays = req.GuideHoldDays,
                    TrailingActivationPct = req.TrailingActivationPct,
                    TrailingDistancePct = req.TrailingDistancePct,
                });
                return Results.Ok();
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        return api;
    }
}
