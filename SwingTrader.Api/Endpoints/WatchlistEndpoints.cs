using System.ComponentModel.DataAnnotations;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;

namespace SwingTrader.Api.Endpoints;

public static class WatchlistEndpoints
{
    public static RouteGroupBuilder MapWatchlistEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/watchlist", async (IWatchlistRepository watchlist, IAccountContext ctx) =>
            Results.Ok(await watchlist.GetActiveAsync(ctx.AccountId)));

        // Multiple named watchlists — see IWatchlistRepository for the caps
        // (WatchlistLimits.MaxEnabledWatchlists / MaxSymbolsPerWatchlist) enforced
        // server-side regardless of what the UI lets through.
        var watchlistsGroup = api.MapGroup("/watchlists");

        watchlistsGroup.MapGet("/", async (IWatchlistRepository watchlists, IAccountContext ctx) =>
            Results.Ok(await watchlists.GetAllWatchlistsAsync(ctx.AccountId)));

        watchlistsGroup.MapGet("/enabled-symbols", async (IWatchlistRepository watchlists, IAccountContext ctx) =>
            Results.Ok(await watchlists.GetAllEnabledSymbolsAsync(ctx.AccountId)));

        // Account-level target size for the weekly AI-managed watchlist refresh
        // (how many symbols Claude picks). Lives on the Account, not the risk
        // profile - it doesn't vary by market regime.
        watchlistsGroup.MapGet("/target-size", async (IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();
            return Results.Ok(new
            {
                account.TargetWatchlistSize,
                Min = Core.Constants.CapitalRules.MinTargetWatchlistSize,
                Max = Core.Constants.CapitalRules.MaxTargetWatchlistSize,
            });
        });

        watchlistsGroup.MapPut("/target-size", async (
            UpdateWatchlistTargetSizeRequest req, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            if (req.TargetWatchlistSize < Core.Constants.CapitalRules.MinTargetWatchlistSize
                || req.TargetWatchlistSize > Core.Constants.CapitalRules.MaxTargetWatchlistSize)
                return Results.BadRequest(new
                {
                    message = $"Watchlist size must be {Core.Constants.CapitalRules.MinTargetWatchlistSize}-{Core.Constants.CapitalRules.MaxTargetWatchlistSize} symbols.",
                });

            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();
            account.TargetWatchlistSize = req.TargetWatchlistSize;
            await accounts.UpdateAsync(account, ct);
            return Results.Ok();
        });

        // Account-level size for the weekly QUALITATIVE watchlist refresh (how
        // many symbols Claude picks on narrative grounds). Separate, smaller
        // knob from the technical target size above.
        watchlistsGroup.MapGet("/qualitative-size", async (IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();
            return Results.Ok(new
            {
                account.QualitativeWatchlistSize,
                Min = Core.Constants.CapitalRules.MinQualitativeWatchlistSize,
                Max = Core.Constants.CapitalRules.MaxQualitativeWatchlistSize,
            });
        });

        watchlistsGroup.MapPut("/qualitative-size", async (
            UpdateQualitativeWatchlistSizeRequest req, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            if (req.QualitativeWatchlistSize < Core.Constants.CapitalRules.MinQualitativeWatchlistSize
                || req.QualitativeWatchlistSize > Core.Constants.CapitalRules.MaxQualitativeWatchlistSize)
                return Results.BadRequest(new
                {
                    message = $"Qualitative watchlist size must be {Core.Constants.CapitalRules.MinQualitativeWatchlistSize}-{Core.Constants.CapitalRules.MaxQualitativeWatchlistSize} symbols.",
                });

            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();
            account.QualitativeWatchlistSize = req.QualitativeWatchlistSize;
            await accounts.UpdateAsync(account, ct);
            return Results.Ok();
        });

        // The full screening universe (symbol + company name) the AI-managed
        // watchlists draw from - shown on the Stock List Universe tab so users
        // can see the whole pool at their disposal. Not account-specific.
        watchlistsGroup.MapGet("/universe", async (IMarketUniverseService universe, CancellationToken ct) =>
            Results.Ok(await universe.GetUniverseWithNamesAsync(ct)));

        // Second-hop economic links (docs/second-hop-plan) - platform-level,
        // Claude-built, deliberately human-auditable: readable by anyone on
        // the account, suppressible by the Owner (the hallucinated-link kill
        // switch). Suppressed links stay visible so the veto is reviewable.
        watchlistsGroup.MapGet("/links/{symbol}", async (
            string symbol, IEconomicLinkRepository links, CancellationToken ct) =>
            Results.Ok(await links.GetLinksAsync(symbol, ct)));

        watchlistsGroup.MapPost("/links/{linkId:long}/suppress", async (
            long linkId, bool suppressed, IEconomicLinkRepository links, IAccountContext ctx, CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            return await links.SetSuppressedAsync(linkId, suppressed, ct)
                ? Results.Ok()
                : Results.NotFound();
        });

        watchlistsGroup.MapPost("/", async (
            CreateWatchlistRequest req,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { message = "Name cannot be empty." });

            var created = await watchlists.CreateWatchlistAsync(ctx.AccountId, req.Name.Trim(), req.Type, req.Description);
            return Results.Ok(created);
        });

        watchlistsGroup.MapPut("/{id:int}", async (
            int id,
            UpdateWatchlistRequest req,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { message = "Name cannot be empty." });

            try
            {
                await watchlists.UpdateWatchlistAsync(ctx.AccountId, id, req.Name.Trim(), req.Description, req.TopMoversEnabled);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        watchlistsGroup.MapDelete("/{id:int}", async (
            int id,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            try
            {
                await watchlists.DeleteWatchlistAsync(ctx.AccountId, id);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        watchlistsGroup.MapPost("/{id:int}/enable", async (
            int id,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            try
            {
                await watchlists.EnableWatchlistAsync(ctx.AccountId, id);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        watchlistsGroup.MapPost("/{id:int}/disable", async (
            int id,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            try
            {
                await watchlists.DisableWatchlistAsync(ctx.AccountId, id);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        watchlistsGroup.MapPost("/{id:int}/set-default", async (
            int id,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            try
            {
                await watchlists.SetDefaultWatchlistAsync(ctx.AccountId, id);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        watchlistsGroup.MapGet("/{id:int}/symbols", async (
            int id,
            IWatchlistRepository watchlists,
            IAccountContext ctx) =>
            Results.Ok(await watchlists.GetSymbolsAsync(ctx.AccountId, id)));

        // Latest pick rationale per symbol on a list - the "[Archetype] reason"
        // the AI recorded in WatchlistHistory when it added the symbol. Surfaced
        // so the review-before-enable decision isn't made blind.
        watchlistsGroup.MapGet("/{id:int}/rationales", async (
            int id,
            IWatchlistRepository watchlists,
            IWatchlistHistoryRepository history,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var symbols = (await watchlists.GetSymbolsAsync(ctx.AccountId, id, ct))
                .Select(i => i.Symbol).ToList();
            if (symbols.Count == 0) return Results.Ok(new Dictionary<string, string>());
            return Results.Ok(await history.GetLatestReasonsAsync(ctx.AccountId, symbols, ct));
        });

        watchlistsGroup.MapPost("/{id:int}/symbols", async (
            int id,
            AddWatchlistSymbolRequest req,
            IWatchlistRepository watchlists,
            IUserHttpClientFactory clientFactory,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Symbol))
                return Results.BadRequest(new { message = "Symbol cannot be empty." });

            var symbol = req.Symbol.Trim().ToUpperInvariant();
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);

            FinnhubQuoteResponse quote;
            FinnhubCompanyProfileResponse profile;
            try
            {
                quote = await finnhub.GetQuoteAsync(symbol);
                profile = await finnhub.GetCompanyProfileAsync(symbol);
            }
            catch (Exception)
            {
                return Results.BadRequest(new { message = $"Could not verify symbol '{symbol}' with Finnhub." });
            }

            if (quote.CurrentPrice is null or 0)
                return Results.BadRequest(new { message = $"Symbol '{symbol}' not found on Finnhub." });

            try
            {
                var item = await watchlists.AddSymbolAsync(
                    ctx.AccountId, id, symbol,
                    string.IsNullOrWhiteSpace(profile.Name) ? symbol : profile.Name,
                    string.IsNullOrWhiteSpace(profile.Industry) ? "Unknown" : profile.Industry,
                    ct);
                return Results.Ok(item);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireRateLimiting(RateLimitPolicies.ExternalRead);

        watchlistsGroup.MapDelete("/{id:int}/symbols/{symbol}", async (
            int id,
            string symbol,
            IWatchlistRepository watchlists,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            await watchlists.RemoveSymbolAsync(ctx.AccountId, id, symbol, ct);
            return Results.Ok();
        });

        // Force a single item straight into research regardless of whether
        // its parent watchlist is enabled - bypasses the stock screener the
        // same way any active watchlist item already does.
        watchlistsGroup.MapPost("/{id:int}/symbols/{symbol}/force", async (
            int id,
            string symbol,
            ForceWatchlistSymbolRequest req,
            IWatchlistRepository watchlists,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner) return Results.Forbid();
            try
            {
                await watchlists.SetForceIntoFinalListAsync(ctx.AccountId, id, symbol, req.Force, ct);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        return api;
    }
}
