using Azure.Identity;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using SwingTrader.Api.Auth;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.HealthChecks;
using SwingTrader.Api.Middleware;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// Key Vault (production only — local dev uses dotnet user-secrets)
// Wrapped in try/catch: a Key Vault outage or RBAC propagation delay should
// degrade to an unhealthy /health/ready response, not crash the whole
// process before Serilog even exists to log why.
var keyVaultUrl = builder.Configuration["KeyVaultUrl"];
if (!string.IsNullOrEmpty(keyVaultUrl))
{
    try
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri(keyVaultUrl),
            new DefaultAzureCredential());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Failed to load Key Vault config from {keyVaultUrl}: {ex}");
    }
}

// Serilog
builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration);
    var aiConnectionString = ctx.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrEmpty(aiConnectionString))
        cfg.WriteTo.ApplicationInsights(aiConnectionString, TelemetryConverter.Traces);
});

// EF Core with SQL Server
builder.Services.AddDbContext<SwingTraderDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<TradingApiHealthCheck>("trading212", tags: ["ready", "external"])
    .AddCheck<FinnhubHealthCheck>("finnhub", tags: ["ready", "external"])
    .AddCheck<ClaudeHealthCheck>("claude", tags: ["ready", "external"])
    .AddCheck<WorkerHealthCheck>("workers", tags: ["live"]);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS for the Angular dev server
builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
        policy.WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

// Microsoft Entra External ID (CIAM) authentication. Authority/Audience come
// from Key Vault (AzureAdB2C--Authority / AzureAdB2C--Audience) - empty
// locally/before Phase 10c's manual setup is completed, in which case every
// request will correctly fail auth rather than silently succeeding.
// Audience is the "Expose an API" App ID URI (api://{clientId}), not the
// bare client ID, since the SPA requests a custom access_as_user scope
// rather than an ID token.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Without this, JwtSecurityTokenHandler remaps "sub" to the long
        // XML-schema ClaimTypes.NameIdentifier URI, so every
        // FindFirst("sub")/FindFirst("email") lookup below silently returns
        // null instead of throwing - which surfaced as a NULL UserId insert
        // into AppUsers rather than an obvious auth failure.
        options.MapInboundClaims = false;
        options.Authority = builder.Configuration["AzureAdB2C:Authority"];
        options.Audience = builder.Configuration["AzureAdB2C:Audience"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            NameClaimType = "name",
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.Requirements.Add(new AdminRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, AdminHandler>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAccountContext, AccountContext>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IAccountInviteRepository, AccountInviteRepository>();
builder.Services.AddScoped<IWatchlistRepository, WatchlistRepository>();
builder.Services.AddScoped<IStrategyWeightsRepository, StrategyWeightsRepository>();
builder.Services.AddScoped<IUserApiKeyRepository, UserApiKeyRepository>();
builder.Services.AddScoped<IJobLogRepository, JobLogRepository>();
builder.Services.AddScoped<INotificationRecipientRepository, NotificationRecipientRepository>();
builder.Services.AddScoped<IKeyEncryptionService, KeyEncryptionService>();
builder.Services.AddScoped<IUserKeyService, UserKeyService>();
builder.Services.AddScoped<IUserHttpClientFactory, UserHttpClientFactory>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<IPortfolioRepository, PortfolioRepository>();
builder.Services.AddScoped<IWorkerHeartbeatRepository, WorkerHeartbeatRepository>();

var app = builder.Build();

// Apply migrations on startup. Wrapped in try/catch: a transient DB outage
// or connection-string issue should surface as an unhealthy /health/ready
// response via DatabaseHealthCheck, not crash the whole process.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<SwingTraderDbContext>();
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Failed to apply EF migrations: {ex}");
    }
}

app.UseSerilogRequestLogging();
app.UseCors("Angular");

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<UserRegistrationMiddleware>();

// Health endpoints (public — no auth required)
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready")
});
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("live")
});

// Swagger (dev only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Protected API surface. Refinement/readiness/run endpoints still land once
// those agents are ported.
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/status", async (IWorkerHeartbeatRepository heartbeats) =>
    Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, workers = await heartbeats.GetAllAsync() }));

api.MapGet("/watchlist", async (IWatchlistRepository watchlist, IAccountContext ctx) =>
    Results.Ok(await watchlist.GetActiveAsync(ctx.AccountId)));

// Grouped by Recommendation to match the Angular signal board's buy/watch/hold/avoid columns.
api.MapGet("/signals/today", async (ISignalRepository signals, IAccountContext ctx) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var todaysSignals = (await signals.GetByDateAsync(ctx.AccountId, today)).ToList();
    return Results.Ok(new
    {
        date = today,
        buy = todaysSignals.Where(s => s.Recommendation == Recommendation.Buy),
        watch = todaysSignals.Where(s => s.Recommendation == Recommendation.Watch),
        hold = todaysSignals.Where(s => s.Recommendation == Recommendation.Hold),
        avoid = todaysSignals.Where(s => s.Recommendation == Recommendation.Avoid),
    });
});

api.MapGet("/trades/recent", async (int? days, ITradeRepository trades, IAccountContext ctx) =>
{
    var from = DateTime.UtcNow.AddDays(-(days ?? 30));
    return Results.Ok(await trades.GetTradeHistoryAsync(ctx.AccountId, from, DateTime.UtcNow));
});

// Open Trade rows double as "positions" - there's no separate live-pricing
// pass in the request path, so CurrentPrice/UnrealizedPnl aren't included
// here; the Monitor Agent updates TrailingStopPrice out of band.
api.MapGet("/positions", async (ITradeRepository trades, IAccountContext ctx) =>
    Results.Ok(await trades.GetOpenTradesAsync(ctx.AccountId)));

api.MapGet("/portfolio", async (IPortfolioRepository portfolio, IAccountContext ctx) =>
{
    var snapshot = await portfolio.GetLatestSnapshotAsync(ctx.AccountId);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

// Approve endpoint stays public - the token in the query string IS the auth.
// Not yet implemented (lands with Agents/Infrastructure porting).

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
        ExpiresAt = DateTime.UtcNow.AddDays(7),
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
    });
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

// Per-account API key storage (Phase 10d). GetKeyStatuses never returns the
// actual key values - only status - since these are third-party trading/
// data credentials.
api.MapGet("/keys", async (IUserKeyService keys, IAccountContext ctx) =>
    Results.Ok(await keys.GetKeyStatusesAsync(ctx.AccountId)));

api.MapPost("/keys/{provider}", async (
    string provider,
    SaveKeyRequest req,
    IUserKeyService keys,
    IAccountContext ctx) =>
{
    if (!ApiKeyProviders.All.Contains(provider))
        return Results.BadRequest(new { message = $"Unknown provider '{provider}'." });
    if (string.IsNullOrWhiteSpace(req.Value))
        return Results.BadRequest(new { message = "Value cannot be empty." });

    await keys.SaveKeyAsync(ctx.AccountId, provider, req.Value);
    var isValid = await keys.TestKeyAsync(ctx.AccountId, provider);
    return Results.Ok(new { valid = isValid });
});

api.MapGet("/keys/{provider}/test", async (
    string provider,
    IUserKeyService keys,
    IAccountContext ctx) =>
{
    var isValid = await keys.TestKeyAsync(ctx.AccountId, provider);
    return Results.Ok(new { valid = isValid });
});

api.MapDelete("/keys/{provider}", async (
    string provider,
    IUserKeyService keys,
    IAccountContext ctx) =>
{
    await keys.DeleteKeyAsync(ctx.AccountId, provider);
    return Results.Ok();
});

// Trading config, notifications, and account lifecycle (Phase 10d Settings page)
api.MapPut("/account/trading-config", async (
    UpdateTradingConfigRequest req,
    IAccountRepository accounts,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();

    var account = await accounts.GetAsync(ctx.AccountId)
        ?? throw new InvalidOperationException("Authenticated caller has no account.");
    account.TradingMode = req.TradingMode;
    account.ApprovalRequired = req.ApprovalRequired;
    await accounts.UpdateAsync(account);
    return Results.Ok();
});

api.MapPut("/account/global-refinement-optin/{enabled:bool}", async (
    bool enabled,
    IAccountRepository accounts,
    IAccountContext ctx) =>
{
    var account = await accounts.GetAsync(ctx.AccountId)
        ?? throw new InvalidOperationException("Authenticated caller has no account.");
    account.GlobalRefinementOptIn = enabled;
    await accounts.UpdateAsync(account);
    return Results.Ok();
});

api.MapGet("/account/notifications", async (INotificationRecipientRepository recipients, IAccountContext ctx) =>
    Results.Ok(await recipients.ListAsync(ctx.AccountId)));

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
        return Results.Forbid();

    await recipients.RemoveAsync(ctx.AccountId, recipientId);
    return Results.Ok();
});

// Soft-delete only - see Account.IsDeleted for why a hard delete isn't
// feasible without cascading through every scoped table.
api.MapDelete("/account", async (IAccountRepository accounts, IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();

    var account = await accounts.GetAsync(ctx.AccountId)
        ?? throw new InvalidOperationException("Authenticated caller has no account.");
    account.IsDeleted = true;
    await accounts.UpdateAsync(account);
    return Results.Ok();
});

// Admin area — global across all accounts, gated by Admin:UserId (a single
// B2C object ID), a separate concept from AccountRole.Owner.
app.MapGroup("/api/admin")
    .RequireAuthorization("Admin")
    .MapGet("/me", () => Results.Ok(new { isAdmin = true }));

// Angular static files (Phase 10b populates wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

// SPA fallback for client-side routes only. A plain MapFallbackToFile
// would also catch unmatched /api, /health, /run, /approve requests and
// serve index.html (200 OK, text/html) instead of a real 404 - which is
// exactly what every not-yet-implemented /api/* endpoint hit until this
// was excluded, breaking Angular's JSON parsing.
var reservedPrefixes = new[] { "/api", "/health", "/run", "/approve", "/swagger" };
app.MapFallback(async context =>
{
    if (reservedPrefixes.Any(p => context.Request.Path.StartsWithSegments(p)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(
        Path.Combine(app.Environment.WebRootPath, "index.html"));
});

app.Run();
