using System.ComponentModel.DataAnnotations;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class RiskProfileEndpoints
{
    public static RouteGroupBuilder MapRiskProfileEndpoints(this RouteGroupBuilder api)
    {
        // Risk profile: per-account capital allocation / position sizing / tier-unlock
        // settings, adjustable within hard safety bounds (SwingTrader.Core.Constants.CapitalRules
        // Min*/Max* range constants). Buy/Watch/StopLoss thresholds live on StrategyWeights
        // (see /strategy-weights above) and are surfaced here read-only for a single unified view.
        api.MapGet("/risk-profile", async (
            IAccountRiskProfileRepository riskProfileRepo,
            IStrategyWeightsRepository weightsRepo,
            IPortfolioRepository portfolioRepo,
            IAccountRepository accounts,
            IAccountContext ctx) =>
        {
            var profile = await riskProfileRepo.GetAsync(ctx.AccountId);
            var weights = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
            var account = await accounts.GetAsync(ctx.AccountId);
            var snapshot = account is not null
                ? await portfolioRepo.GetLatestSnapshotAsync(ctx.AccountId, account.TradingMode)
                : null;

            return Results.Ok(new
            {
                profile.LockedCapitalPct,
                profile.MaxPositionPctOfActive,
                profile.MaxOpenPositions,
                profile.DailyLossCircuitBreakerPct,
                profile.Tier1UnlockMinTrades,
                profile.Tier1UnlockMinWinRate,
                profile.Tier2UnlockMinTrades,
                profile.Tier2UnlockMinWinRate,
                profile.MaxHoldDays,
                profile.TrailingActivationPct,
                profile.TrailingDistancePct,
                profile.EarningsGateDays,
                profile.MinHoldDays,
                profile.MomentumHealthThreshold,
                profile.TargetWatchlistSize,
                profile.AutopauseDuringBear,
                profile.RiskLabel,
                BuyThreshold = weights?.BuyThreshold,
                WatchThreshold = weights?.WatchThreshold,
                StopLossPctDefault = weights?.StopLossPctDefault,
                CapitalBreakdown = snapshot is null ? null : new
                {
                    TotalCapital = snapshot.TotalCapital,
                    LockedCapital = snapshot.TotalCapital * profile.LockedCapitalPct,
                    // Computed from tier x total rather than read from the
                    // snapshot: snapshot.ActiveCapital is only written by
                    // execution runs, so an account that hasn't executed yet
                    // showed GBP 0 "active capital" here - nonsense for a
                    // settings preview that's explaining how sizing works.
                    ActiveCapital = snapshot.TotalCapital * TierPct(snapshot.CurrentTier),
                    MaxPerTrade = snapshot.TotalCapital * TierPct(snapshot.CurrentTier) * profile.MaxPositionPctOfActive,
                    CurrentTier = snapshot.CurrentTier,
                },
                AllowedRanges = new
                {
                    LockedCapitalPct = new { Min = CapitalRules.MinLockedCapitalPct, Max = CapitalRules.MaxLockedCapitalPct },
                    MaxPositionPctOfActive = new { Min = CapitalRules.MinMaxPositionPctOfActive, Max = CapitalRules.MaxMaxPositionPctOfActive },
                    MaxOpenPositions = new { Min = CapitalRules.MinMaxOpenPositions, Max = CapitalRules.MaxMaxOpenPositions },
                    DailyLossCircuitBreakerPct = new { Min = CapitalRules.MinDailyLossCircuitBreakerPct, Max = CapitalRules.MaxDailyLossCircuitBreakerPct },
                    Tier1UnlockMinTrades = new { Min = CapitalRules.MinTier1UnlockMinTrades, Max = CapitalRules.MaxTier1UnlockMinTrades },
                    Tier1UnlockMinWinRate = new { Min = CapitalRules.MinTier1UnlockMinWinRate, Max = CapitalRules.MaxTier1UnlockMinWinRate },
                    Tier2UnlockMinTrades = new { Min = CapitalRules.MinTier2UnlockMinTrades, Max = CapitalRules.MaxTier2UnlockMinTrades },
                    Tier2UnlockMinWinRate = new { Min = CapitalRules.MinTier2UnlockMinWinRate, Max = CapitalRules.MaxTier2UnlockMinWinRate },
                    MaxHoldDays = new { Min = CapitalRules.MinMaxHoldDays, Max = CapitalRules.MaxMaxHoldDays },
                    TrailingActivationPct = new { Min = CapitalRules.MinTrailingActivationPct, Max = CapitalRules.MaxTrailingActivationPct },
                    TrailingDistancePct = new { Min = CapitalRules.MinTrailingDistancePct, Max = CapitalRules.MaxTrailingDistancePct },
                    EarningsGateDays = new { Min = CapitalRules.MinEarningsGateDays, Max = CapitalRules.MaxEarningsGateDays },
                    MinHoldDays = new { Min = CapitalRules.AbsoluteMinHoldDays, Max = profile.MaxHoldDays - 1 },
                    MomentumHealthThreshold = new { Min = CapitalRules.MinMomentumHealthThreshold, Max = CapitalRules.MaxMomentumHealthThreshold },
                    TargetWatchlistSize = new { Min = CapitalRules.MinTargetWatchlistSize, Max = CapitalRules.MaxTargetWatchlistSize },
                },
            });
        });

        api.MapPut("/risk-profile", async (
            UpdateRiskProfileRequest req,
            IAccountRiskProfileRepository riskProfileRepo,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            try
            {
                await riskProfileRepo.UpdateAsync(new AccountRiskProfile
                {
                    AccountId = ctx.AccountId,
                    LockedCapitalPct = req.LockedCapitalPct,
                    MaxPositionPctOfActive = req.MaxPositionPctOfActive,
                    MaxOpenPositions = req.MaxOpenPositions,
                    DailyLossCircuitBreakerPct = req.DailyLossCircuitBreakerPct,
                    Tier1UnlockMinTrades = req.Tier1UnlockMinTrades,
                    Tier1UnlockMinWinRate = req.Tier1UnlockMinWinRate,
                    Tier2UnlockMinTrades = req.Tier2UnlockMinTrades,
                    Tier2UnlockMinWinRate = req.Tier2UnlockMinWinRate,
                    MaxHoldDays = req.MaxHoldDays,
                    TrailingActivationPct = req.TrailingActivationPct,
                    TrailingDistancePct = req.TrailingDistancePct,
                    EarningsGateDays = req.EarningsGateDays,
                    MinHoldDays = req.MinHoldDays,
                    MomentumHealthThreshold = req.MomentumHealthThreshold,
                    TargetWatchlistSize = req.TargetWatchlistSize,
                    AutopauseDuringBear = req.AutopauseDuringBear,
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

        api.MapPost("/risk-profile/reset", async (
            IAccountRiskProfileRepository riskProfileRepo,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var reset = await riskProfileRepo.ResetToDefaultsAsync(ctx.AccountId);
            return Results.Ok(reset);
        });

        return api;
    }

    // The tier's active-capital share of the whole account - the same mapping
    // PositionSizingService uses to budget live positions.
    private static decimal TierPct(CapitalTier tier) => tier switch
    {
        CapitalTier.Tier2 => CapitalRules.Tier2CapitalPct,
        CapitalTier.Tier3 => CapitalRules.Tier3CapitalPct,
        _ => CapitalRules.Tier1CapitalPct,
    };
}
