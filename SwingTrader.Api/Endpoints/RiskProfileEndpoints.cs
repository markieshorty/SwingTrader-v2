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
            IAccountContext ctx,
            MarketRegime? regime) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId);
            // Which book to show: the requested regime, else the account's
            // currently-detected live regime.
            var selectedRegime = regime ?? account?.CurrentMarketRegime ?? MarketRegime.Neutral;
            var profile = await riskProfileRepo.GetAsync(ctx.AccountId, selectedRegime);
            var weights = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
            var snapshot = account is not null
                ? await portfolioRepo.GetLatestSnapshotAsync(ctx.AccountId, account.TradingMode)
                : null;

            // Whether the Default master book is on (governs live regardless of
            // the detected regime) - drives the [LIVE] capsule in the UI.
            var defaultEnabled = await riskProfileRepo.IsDefaultRegimeEnabledAsync(ctx.AccountId);

            return Results.Ok(new
            {
                Regime = profile.Regime.ToString(),
                CurrentRegime = (account?.CurrentMarketRegime ?? MarketRegime.Neutral).ToString(),
                RegimeUpdatedAt = account?.RegimeUpdatedAt,
                AvailableRegimes = new[] { "Default", "Bull", "Neutral", "Bear", "Crisis" },
                DefaultRegimeEnabled = defaultEnabled,
                profile.Enabled,
                profile.AutopauseTrading,
                profile.LockedCapitalPct,
                profile.MaxOpenPositions,
                profile.DailyLossCircuitBreakerPct,
                profile.MaxHoldDays,
                profile.TrailingActivationPct,
                profile.TrailingDistancePct,
                profile.EarningsGateDays,
                profile.MinHoldDays,
                profile.MomentumHealthThreshold,
                profile.StopLossPct,
                profile.TargetPct,
                SizingMode = profile.SizingMode.ToString(),
                profile.FlatPositionPct,
                profile.SizingAggressiveness,
                profile.ForwardVetoFloor,
                profile.RiskLabel,
                BuyThreshold = weights?.BuyThreshold,
                WatchThreshold = weights?.WatchThreshold,
                StopLossPctDefault = weights?.StopLossPctDefault,
                CapitalBreakdown = snapshot is null ? null : new
                {
                    TotalCapital = snapshot.TotalCapital,
                    LockedCapital = snapshot.TotalCapital * profile.LockedCapitalPct,
                    // Deployable = the un-locked share (no tier pool now). Each
                    // position is FlatPositionPct of the whole portfolio.
                    ActiveCapital = snapshot.TotalCapital * (1m - profile.LockedCapitalPct),
                    MaxPerTrade = snapshot.TotalCapital * profile.FlatPositionPct,
                },
                AllowedRanges = new
                {
                    LockedCapitalPct = new { Min = CapitalRules.MinLockedCapitalPct, Max = CapitalRules.MaxLockedCapitalPct },
                    MaxOpenPositions = new { Min = CapitalRules.MinMaxOpenPositions, Max = CapitalRules.MaxMaxOpenPositions },
                    DailyLossCircuitBreakerPct = new { Min = CapitalRules.MinDailyLossCircuitBreakerPct, Max = CapitalRules.MaxDailyLossCircuitBreakerPct },
                    MaxHoldDays = new { Min = CapitalRules.MinMaxHoldDays, Max = CapitalRules.MaxMaxHoldDays },
                    TrailingActivationPct = new { Min = CapitalRules.MinTrailingActivationPct, Max = CapitalRules.MaxTrailingActivationPct },
                    TrailingDistancePct = new { Min = CapitalRules.MinTrailingDistancePct, Max = CapitalRules.MaxTrailingDistancePct },
                    EarningsGateDays = new { Min = CapitalRules.MinEarningsGateDays, Max = CapitalRules.MaxEarningsGateDays },
                    MinHoldDays = new { Min = CapitalRules.AbsoluteMinHoldDays, Max = profile.MaxHoldDays - 1 },
                    MomentumHealthThreshold = new { Min = CapitalRules.MinMomentumHealthThreshold, Max = CapitalRules.MaxMomentumHealthThreshold },
                    StopLossPct = new { Min = CapitalRules.MinStopLossPct, Max = CapitalRules.MaxStopLossPct },
                    TargetPct = new { Min = CapitalRules.MinTargetPct, Max = CapitalRules.MaxTargetPct },
                    FlatPositionPct = new { Min = CapitalRules.MinFlatPositionPct, Max = CapitalRules.MaxFlatPositionPct },
                    SizingAggressiveness = new { Min = CapitalRules.MinSizingAggressiveness, Max = CapitalRules.MaxSizingAggressiveness },
                    ForwardVetoFloor = new { Min = CapitalRules.MinForwardVetoFloor, Max = CapitalRules.MaxForwardVetoFloor },
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
                    Regime = req.Regime,
                    Enabled = req.Enabled,
                    LockedCapitalPct = req.LockedCapitalPct,
                    MaxOpenPositions = req.MaxOpenPositions,
                    DailyLossCircuitBreakerPct = req.DailyLossCircuitBreakerPct,
                    MaxHoldDays = req.MaxHoldDays,
                    TrailingActivationPct = req.TrailingActivationPct,
                    TrailingDistancePct = req.TrailingDistancePct,
                    EarningsGateDays = req.EarningsGateDays,
                    MinHoldDays = req.MinHoldDays,
                    MomentumHealthThreshold = req.MomentumHealthThreshold,
                    AutopauseTrading = req.AutopauseTrading,
                    StopLossPct = req.StopLossPct,
                    TargetPct = req.TargetPct,
                    SizingMode = Enum.TryParse<PositionSizingMode>(req.SizingMode, ignoreCase: true, out var mode)
                        ? mode
                        : PositionSizingMode.Flat,
                    FlatPositionPct = req.FlatPositionPct,
                    SizingAggressiveness = req.SizingAggressiveness,
                    ForwardVetoFloor = req.ForwardVetoFloor,
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
            IAccountContext ctx,
            MarketRegime? regime) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            var reset = await riskProfileRepo.ResetToDefaultsAsync(ctx.AccountId, regime ?? MarketRegime.Neutral);
            return Results.Ok(reset);
        });

        return api;
    }
}
