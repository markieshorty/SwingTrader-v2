using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Monitor.Query;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using SwingTrader.Api;
using SwingTrader.Api.Auth;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Endpoints;
using SwingTrader.Api.HealthChecks;
using SwingTrader.Api.Middleware;
using SwingTrader.Api.Services;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Agents.Monitor;
using SwingTrader.Agents.Readiness;
using SwingTrader.Agents.Refinement;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.Security;
using SwingTrader.Infrastructure.Services;

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

// Serialize enums as their string name (e.g. KeyStatus.NotSet -> "NotSet")
// rather than System.Text.Json's default raw integer - the Angular DTOs
// compare against string literals ('NotSet', 'Valid', etc.), so left at the
// default every enum-valued response silently mismatched on the frontend.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

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

// Rate limiting for endpoints that fan out to paid external APIs. Partitioned
// by authenticated user (the "sub" claim) so one account can't exhaust
// another's budget, and so the limit is a real per-user cost ceiling rather
// than a shared global one. Two named policies:
//   claude-jobs   - POST /run/{jobType}: each manual research/report/
//                   refinement run fans out many Claude calls (the user's own
//                   Anthropic spend), so this is deliberately tight. The
//                   automated scheduler is the intended trigger; manual runs
//                   are for testing, where a handful per window is plenty and
//                   an accidental double-click or a script can't rack up a bill.
//   external-read - the GET endpoints that pull live Finnhub/Tiingo/T212
//                   market data per request: looser, just a sanity ceiling
//                   against a hot-looping client burning provider quota.
// Rejections return 429 rather than silently queuing (QueueLimit = 0).
// Policy names live in RateLimitPolicies so the registration here and the
// per-endpoint RequireRateLimiting(...) calls can never drift apart.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string PartitionKey(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    options.AddPolicy(RateLimitPolicies.ClaudeJobs, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));

    options.AddPolicy(RateLimitPolicies.ExternalRead, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
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
builder.Services.AddScoped<IAccountRiskProfileRepository, AccountRiskProfileRepository>();
builder.Services.AddScoped<IAdminLogRepository, AdminLogRepository>();
builder.Services.AddScoped<IAdminRepository, AdminRepository>();
builder.Services.AddScoped<IMonitoringRepository, MonitoringRepository>();
builder.Services.AddScoped<MonitoringService>();
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
builder.Services.AddScoped<IActivityLogRepository, ActivityLogRepository>();
builder.Services.AddScoped<IApprovalRepository, ApprovalRepository>();
builder.Services.AddScoped<IRefinementSuggestionRepository, RefinementSuggestionRepository>();
builder.Services.AddScoped<ISystemChecklistRepository, SystemChecklistRepository>();
builder.Services.AddScoped<IReadinessSnapshotRepository, ReadinessSnapshotRepository>();
builder.Services.AddScoped<IReadinessAssessmentService, ReadinessAssessmentService>();
builder.Services.AddScoped<IApplyRefinementService, ApplyRefinementService>();
builder.Services.Configure<RefinementConfig>(builder.Configuration.GetSection(RefinementConfig.SectionName));
builder.Services.Configure<RiskManagementConfig>(builder.Configuration.GetSection(RiskManagementConfig.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IMarketCalendarService, MarketCalendarService>();
builder.Services.AddScoped<AccountViewService>();
builder.Services.AddScoped<StrategyLabService>();
builder.Services.AddScoped<StrategyLabAnalysisService>();
builder.Services.Configure<ClaudeConfig>(builder.Configuration.GetSection(ClaudeConfig.SectionName));
builder.Services.Configure<FilingDeltaConfig>(builder.Configuration.GetSection(FilingDeltaConfig.SectionName));
// The "Close early" endpoint reuses the Monitor worker's exit path (market
// sell + trade close + exit email + same-day execution re-enqueue) so a
// manual close behaves identically to a rule-driven one.
builder.Services.Configure<ExecutionConfig>(builder.Configuration.GetSection(ExecutionConfig.SectionName));
builder.Services.Configure<EmailConfig>(builder.Configuration.GetSection(EmailConfig.SectionName));
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPositionExitService, PositionExitService>();
builder.Services.AddScoped<SwingTrader.Agents.Refinement.ITradeReplayService, SwingTrader.Agents.Refinement.TradeReplayService>();
builder.Services.AddScoped<IHistoricalCandleRepository, HistoricalCandleRepository>();
builder.Services.AddScoped<ISentimentArchiveRepository, SentimentArchiveRepository>();
builder.Services.AddScoped<IBacktestRunRepository, BacktestRunRepository>();
builder.Services.AddScoped<IEconomicLinkRepository, EconomicLinkRepository>();
builder.Services.AddScoped<IFilingRepository, FilingRepository>();
builder.Services.AddScoped<IWatchlistHistoryRepository, WatchlistHistoryRepository>();
builder.Services.AddScoped<IMarketRegimeService, MarketRegimeService>();

// Screening universe (S&P 1500 + Nasdaq-100 via Wikipedia) for the /watchlists
// Stock List Universe tab. Mirrors the Functions host's registration; Wikipedia
// needs a descriptive User-Agent per its bot policy.
builder.Services.AddScoped<IMarketUniverseService, MarketUniverseService>();
builder.Services.AddHttpClient<IWikipediaIndexClient, WikipediaIndexClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SwingTraderBot/1.0 (personal swing-trading app; contact via GitHub repo)");
});
builder.Services.AddScoped<IMomentumHealthService, MomentumHealthService>();

// Same managed-identity Service Bus client as the Functions host - the manual
// "run now" endpoints below send onto the same queues the Scheduler enqueues
// onto, so the existing Consumer Functions pick them up identically.
var serviceBusNamespace = builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"];
if (!string.IsNullOrEmpty(serviceBusNamespace))
{
    builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential()));
    // Admin/management client for the monitoring dashboard's queue + dead-letter
    // depths. Reading runtime properties needs the "Azure Service Bus Data Owner"
    // role on the managed identity; MonitoringService degrades gracefully if it's
    // not yet granted.
    builder.Services.AddSingleton(new ServiceBusAdministrationClient(serviceBusNamespace, new DefaultAzureCredential()));
}

// App Insights logs query client for the monitoring dashboard. Needs the
// "Monitoring Reader" role on the insights resource; MonitoringService degrades
// gracefully when the resource id/role isn't configured.
if (!string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ResourceId"]))
{
    builder.Services.AddSingleton(new LogsQueryClient(new DefaultAzureCredential()));
}

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
// After auth so the rate-limiter can partition by the "sub" claim.
app.UseRateLimiter();
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

// ---------------------------------------------------------------------------
// Endpoint registration. Each functional area lives in its own file under
// Endpoints/ as a static extension method (e.g. TradesEndpoints.MapTradesEndpoints).
// The shared groups below carry the exact prefixes, authorization, and
// rate-limiting the endpoints previously had inline, so routing is unchanged.
// ---------------------------------------------------------------------------

// Protected API surface — every /api/* route requires authentication.
var api = app.MapGroup("/api").RequireAuthorization();
api.MapStatusEndpoints();
api.MapSignalsEndpoints();
api.MapTradesEndpoints();
api.MapPortfolioEndpoints();
api.MapRefinementEndpoints();
api.MapReadinessEndpoints();
api.MapApprovalsEndpoints();
api.MapAccountEndpoints();
api.MapNotificationsEndpoints();
api.MapKeysEndpoints();
api.MapStrategyWeightsEndpoints();
api.MapStrategyLabEndpoints();
api.MapRiskProfileEndpoints();
api.MapWatchlistEndpoints();
api.MapIntelligenceEndpoints();

// Manual "run now" triggers (/run/*) and the global admin area (/api/admin/*)
// are separate top-level groups that own their auth / rate-limiting internally.
app.MapRunEndpoints();
app.MapAdminEndpoints();

// Angular static files (Phase 10b populates wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

// SPA fallback for client-side routes only. A plain MapFallbackToFile
// would also catch unmatched /api, /health, /run requests and serve
// index.html (200 OK, text/html) instead of a real 404 - which is exactly
// what every not-yet-implemented /api/* endpoint hit until this was
// excluded, breaking Angular's JSON parsing.
var reservedPrefixes = new[] { "/api", "/health", "/run", "/swagger" };
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
