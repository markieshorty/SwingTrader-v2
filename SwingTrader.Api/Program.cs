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
using SwingTrader.Data;
using SwingTrader.Data.Repositories;

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

// Azure AD B2C authentication. Authority/ClientId come from Key Vault
// (AzureAdB2C--Authority / AzureAdB2C--ClientId) - empty locally/before
// Phase 10c's manual B2C setup is completed, in which case every request
// will correctly fail auth rather than silently succeeding.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["AzureAdB2C:Authority"];
        options.Audience = builder.Configuration["AzureAdB2C:ClientId"];
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

// Protected API surface. Full endpoints (/api/portfolio, /api/positions,
// /api/signals/today, /api/trades/recent, /api/watchlist, /api/refinement/*,
// /api/readiness, /run/*) land once SwingTrader.Agents and
// SwingTrader.Infrastructure business logic is ported in later phases.
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/status", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

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
