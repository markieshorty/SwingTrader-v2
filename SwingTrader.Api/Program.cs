using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using SwingTrader.Api.Auth;
using SwingTrader.Api.Contracts;
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
const string ClaudeJobsPolicy = "claude-jobs";
const string ExternalReadPolicy = "external-read";
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string PartitionKey(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    options.AddPolicy(ClaudeJobsPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
        }));

    options.AddPolicy(ExternalReadPolicy, ctx =>
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
builder.Services.AddScoped<IMarketRegimeService, MarketRegimeService>();
builder.Services.AddScoped<IMomentumHealthService, MomentumHealthService>();

// Same managed-identity Service Bus client as the Functions host - the manual
// "run now" endpoints below send onto the same queues the Scheduler enqueues
// onto, so the existing Consumer Functions pick them up identically.
var serviceBusNamespace = builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"];
if (!string.IsNullOrEmpty(serviceBusNamespace))
{
    builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential()));
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

// Protected API surface. Refinement/readiness/run endpoints still land once
// those agents are ported.
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/status", async (IActivityLogRepository activityLog, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
{
    var account = await accounts.GetAsync(ctx.AccountId, ct);
    if (account is null) return Results.NotFound();

    var entries = await activityLog.GetRecentAsync(ctx.AccountId, account.TradingMode);
    var runs = entries.Select(e => new
    {
        e.Category,
        e.Title,
        e.Result,
        e.Message,
        e.OccurredAt,
    });
    return Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, runs });
});

// Next scheduled run per job type, for the Dashboard's per-job cards -
// mirrors SchedulerFunction's windows (see JobScheduleInfo). Same for every
// account since the Scheduler's windows aren't account-specific.
api.MapGet("/jobs/next-runs", () => Results.Ok(JobScheduleInfo.GetNextRuns(DateTime.UtcNow)));

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

// Raw Trade JSON doesn't carry RealizedPnlPercent/DaysHeld/SetupType/
// ConvictionScoreAtEntry - those aren't columns on the entity, they're
// derived the same way /positions already derives its equivalents below.
// Was previously returning the bare entity, silently leaving the Angular
// TradeDto's percent/days-held/setup columns blank for every closed trade.
api.MapGet("/trades/recent", async (int? days, ITradeRepository trades, ISignalRepository signals, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
{
    var account = await accounts.GetAsync(ctx.AccountId, ct);
    if (account is null) return Results.NotFound();

    var from = DateTime.UtcNow.AddDays(-(days ?? 30));
    var history = await trades.GetTradeHistoryAsync(ctx.AccountId, account.TradingMode, from, DateTime.UtcNow);

    var results = new List<object>();
    foreach (var trade in history)
    {
        var end = trade.ClosedAt ?? DateTime.UtcNow;
        var daysHeld = Math.Max(0, (int)(end - trade.OpenedAt).TotalDays);

        // T212's own RealizedPnl/EntryValueGbp (both real £, post-FX/fees -
        // see MonitorService.ReconcileOrderFillsAsync) are the source of
        // truth once the fill is confirmed. Falls back to a per-share-price
        // estimate only for trades not yet reconciled (or from before this
        // feature existed), which understates/overstates return% by
        // whatever the FX/fee difference was - real but temporary.
        var realizedPnlPercent = trade.RealizedPnl.HasValue && trade.EntryValueGbp is > 0
            ? trade.RealizedPnl.Value / trade.EntryValueGbp.Value * 100m
            : trade.ExitPrice.HasValue && trade.EntryPrice > 0
                ? (trade.ExitPrice.Value - trade.EntryPrice) / trade.EntryPrice * 100m
                : (decimal?)null;

        var signal = trade.SignalId.HasValue ? await signals.GetByIdAsync(ctx.AccountId, trade.SignalId.Value) : null;
        var totalFeesGbp = trade.EntryFeesGbp.HasValue || trade.ExitFeesGbp.HasValue
            ? (trade.EntryFeesGbp ?? 0m) + (trade.ExitFeesGbp ?? 0m)
            : (decimal?)null;

        results.Add(new
        {
            trade.Id,
            trade.Symbol,
            trade.CompanyName,
            Direction = trade.Direction.ToString(),
            trade.EntryPrice,
            trade.ExitPrice,
            trade.EntryValueGbp,
            trade.ExitValueGbp,
            FeesGbp = totalFeesGbp,
            trade.RealizedPnl,
            RealizedPnlPercent = realizedPnlPercent,
            DaysHeld = daysHeld,
            Status = trade.Status.ToString(),
            SetupType = signal?.SetupType.ToString() ?? "Unknown",
            ConvictionScoreAtEntry = signal?.ConvictionScore,
            trade.MarketRegimeAtEntry,
            OpenedAt = trade.OpenedAt,
            trade.ClosedAt,
        });
    }

    return Results.Ok(results);
});

// Open Trade rows double as "positions", enriched here with a live quote
// (for currentPrice/unrealisedPnl) and the originating signal (for
// setupType/convictionScoreAtEntry) - the Angular PositionDto shape needs
// fields (stopLoss, target, daysHeld, etc.) that don't exist by those names
// on the Trade entity itself, so this was previously returning raw Trade
// JSON that silently didn't match what the dashboard's position cards and
// stop-target-bar expected (blank prices/PnL/days-held in the UI).
api.MapGet("/positions", async (
    ITradeRepository trades,
    ISignalRepository signals,
    IWatchlistRepository watchlist,
    IAccountRepository accounts,
    IUserHttpClientFactory clientFactory,
    IAccountContext ctx,
    CancellationToken ct) =>
{
    var account = await accounts.GetAsync(ctx.AccountId, ct);
    if (account is null) return Results.NotFound();

    var openTrades = (await trades.GetOpenTradesAsync(ctx.AccountId, account.TradingMode)).ToList();
    if (openTrades.Count == 0) return Results.Ok(Array.Empty<object>());

    IFinnhubClient? finnhub = null;
    try
    {
        finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
    }
    catch
    {
        // No Finnhub key configured - fall back to entry price below rather
        // than failing the whole positions list.
    }

    var results = new List<object>(openTrades.Count);
    foreach (var trade in openTrades)
    {
        var currentPrice = trade.EntryPrice;
        if (finnhub is not null)
        {
            try
            {
                var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                if (quote.CurrentPrice is > 0) currentPrice = quote.CurrentPrice.Value;
            }
            catch
            {
                // Keep the entry-price fallback - one symbol's quote failure
                // shouldn't blank out the whole positions list.
            }
        }

        var unrealisedPnl = (currentPrice - trade.EntryPrice) * trade.Quantity;
        var unrealisedPnlPercent = trade.EntryPrice > 0 ? (currentPrice - trade.EntryPrice) / trade.EntryPrice * 100m : 0m;
        var daysHeld = Math.Max(0, (int)(DateTime.UtcNow - trade.OpenedAt).TotalDays);

        var signal = trade.SignalId.HasValue ? await signals.GetByIdAsync(ctx.AccountId, trade.SignalId.Value) : null;

        // "Near" the stop/target = within 2% of that boundary price - close
        // enough to flag on the dashboard without being a hard trigger
        // (MonitorService owns the actual stop/target exit logic).
        var isNearStop = trade.StopLossPrice > 0 && Math.Abs(currentPrice - trade.StopLossPrice) / trade.StopLossPrice <= 0.02m;
        var isNearTarget = trade.TargetPrice > 0 && Math.Abs(currentPrice - trade.TargetPrice) / trade.TargetPrice <= 0.02m;

        // Older trades placed before Trade.CompanyName existed fall back to a
        // live watchlist lookup, then the bare symbol if it's since been removed.
        var companyName = trade.CompanyName
            ?? (await watchlist.GetBySymbolAsync(ctx.AccountId, trade.Symbol))?.CompanyName
            ?? trade.Symbol;

        results.Add(new
        {
            trade.Id,
            trade.Symbol,
            CompanyName = companyName,
            trade.EntryPrice,
            CurrentPrice = currentPrice,
            StopLoss = trade.StopLossPrice,
            Target = trade.TargetPrice,
            trade.TrailingStopPrice,
            trade.Quantity,
            UnrealisedPnl = unrealisedPnl,
            UnrealisedPnlPercent = unrealisedPnlPercent,
            DaysHeld = daysHeld,
            EntryDate = trade.OpenedAt,
            SetupType = signal?.SetupType.ToString() ?? "Unknown",
            ConvictionScoreAtEntry = signal?.ConvictionScore,
            trade.MarketRegimeAtEntry,
            IsNearStop = isNearStop,
            IsNearTarget = isNearTarget,
            trade.Phase,
            trade.MomentumHealthScore,
            trade.MomentumHealthVerdict,
            trade.MomentumHealthReasoning,
            trade.MomentumHealthCheckedAt,
            trade.PhaseConfirmedAt,
        });
    }

    return Results.Ok(results);
}).RequireRateLimiting(ExternalReadPolicy);

// Manual/admin trigger — recompute momentum health for a single open
// position outside the normal MinHoldDays schedule. Useful for support and
// for verifying a probation configuration change without waiting a day.
api.MapPost("/positions/{tradeId:int}/check-momentum", async (
    int tradeId,
    ITradeRepository trades,
    IMomentumHealthService momentumHealth,
    IAccountContext ctx,
    CancellationToken ct) =>
{
    var trade = await trades.GetByIdAsync(ctx.AccountId, tradeId);
    if (trade is null) return Results.NotFound();

    var result = await momentumHealth.CalculateAsync(ctx.AccountId, trade, ct);
    return Results.Ok(result);
}).RequireRateLimiting(ExternalReadPolicy);

// Raw PortfolioSnapshot has no todayPnl/todayPnlPercent/winRate30d - the
// Angular PortfolioDto expected these but nothing ever computed them, so
// the Dashboard's Today P&L / 30-Day Win Rate cards always showed
// £0.00/n-a regardless of what actually happened, not just "until sold".
api.MapGet("/portfolio", async (
    IPortfolioRepository portfolio,
    ITradeRepository trades,
    IAccountRepository accounts,
    IUserHttpClientFactory clientFactory,
    IAccountContext ctx,
    CancellationToken ct) =>
{
    var account = await accounts.GetAsync(ctx.AccountId, ct);
    if (account is null) return Results.NotFound();

    var snapshot = await portfolio.GetLatestSnapshotAsync(ctx.AccountId, account.TradingMode);
    if (snapshot is null) return Results.NotFound();

    var allTrades = (await trades.GetAllAsync(ctx.AccountId, account.TradingMode)).ToList();
    var today = DateTime.UtcNow.Date;

    var realizedToday = allTrades
        .Where(t => t.ClosedAt.HasValue && t.ClosedAt.Value.Date == today && t.RealizedPnl.HasValue)
        .Sum(t => t.RealizedPnl!.Value);

    // Today P&L = today's realized P&L + open positions' total unrealized
    // mark-to-market (current price vs entry, not strictly "since market
    // open" for positions held multiple days) - a live, meaningful number
    // rather than always zero mid-day waiting for something to close.
    var openTrades = allTrades.Where(t => t.Status == TradeStatus.Open).ToList();
    var unrealizedOpen = 0m;
    if (openTrades.Count > 0)
    {
        IFinnhubClient? finnhub = null;
        try
        {
            finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
        }
        catch
        {
            // No Finnhub key configured - unrealized contribution stays 0 rather than failing the whole card.
        }

        foreach (var trade in openTrades)
        {
            var currentPrice = trade.EntryPrice;
            if (finnhub is not null)
            {
                try
                {
                    var quote = await finnhub.GetQuoteAsync(trade.Symbol);
                    if (quote.CurrentPrice is > 0) currentPrice = quote.CurrentPrice.Value;
                }
                catch
                {
                    // Keep the entry-price fallback for this one symbol.
                }
            }
            unrealizedOpen += (currentPrice - trade.EntryPrice) * trade.Quantity;
        }
    }

    var todayPnl = realizedToday + unrealizedOpen;
    var todayPnlPercent = snapshot.TotalCapital > 0 ? todayPnl / snapshot.TotalCapital * 100m : 0m;

    var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
    var closedLast30Days = allTrades
        .Where(t => t.ClosedAt.HasValue && t.ClosedAt.Value >= thirtyDaysAgo && t.RealizedPnl.HasValue)
        .ToList();
    var winRate30d = closedLast30Days.Count > 0
        ? (decimal)closedLast30Days.Count(t => t.RealizedPnl > 0) / closedLast30Days.Count * 100m
        : 0m;

    return Results.Ok(new
    {
        snapshot.TotalCapital,
        snapshot.LockedCapital,
        snapshot.ReserveCapital,
        snapshot.ActiveCapital,
        snapshot.CashAvailable,
        snapshot.OpenPositionsValue,
        snapshot.TotalPnl,
        TodayPnl = todayPnl,
        TodayPnlPercent = todayPnlPercent,
        WinRate30d = winRate30d,
        snapshot.CurrentTier,
    });
}).RequireRateLimiting(ExternalReadPolicy);

// Regime is shared market data (cached globally in MarketRegimeService), but
// still needs one account's Finnhub/Tiingo keys to fetch it with.
api.MapGet("/refinement/current-regime", async (
    IMarketRegimeService regimeService,
    IUserHttpClientFactory clientFactory,
    IAccountContext ctx,
    CancellationToken ct) =>
{
    var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
    var tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(ctx.AccountId, ct);
    var result = await regimeService.GetCurrentRegimeAsync(tiingo, finnhub, ct);
    return Results.Ok(new { regime = result.Regime, detectedAt = DateTime.UtcNow });
}).RequireRateLimiting(ExternalReadPolicy);

api.MapGet("/readiness", async (
    IReadinessAssessmentService readiness,
    Microsoft.Extensions.Options.IOptions<RefinementConfig> refinementConfig,
    IAccountContext ctx,
    CancellationToken ct) =>
{
    var report = await readiness.AssessAsync(ctx.AccountId, ct);
    var minRegimeSample = Math.Max(1, refinementConfig.Value.MinRegimeSampleSize);

    int RegimeProgress(MarketRegime regime) =>
        Math.Min(100, report.RegimeTradeCount.GetValueOrDefault(regime, 0) * 100 / minRegimeSample);

    string? FormatRange(DateTime? low, DateTime? high) =>
        low.HasValue && high.HasValue
            ? $"~{low.Value.AddDays((high.Value - low.Value).TotalDays / 2):d MMM yyyy} (range: {low:d MMM}–{high:d MMM yyyy})"
            : null;

    var features = report.Features.Select(f => new
    {
        featureName = f.FeatureName,
        status = f.Status,
        riskLevel = f.RiskLevel,
        criteria = f.Criteria.Select(c => new { label = c.Description, met = c.Met }),
        assessment = f.Assessment,
        estimatedReadyDateRange = FormatRange(f.EstimatedReadyDateLow, f.EstimatedReadyDateHigh),
        actionHint = f.Recommendation ?? string.Empty,
    });

    // Snapshots are daily; collapse to one representative point per ISO week
    // for the trajectory chart, since that's the cadence the frontend expects.
    var weeklySnapshots = report.TrajectoryHistory
        .OrderBy(s => s.SnapshotDate)
        .GroupBy(s => (s.SnapshotDate.Year, System.Globalization.ISOWeek.GetWeekOfYear(s.SnapshotDate.ToDateTime(TimeOnly.MinValue))))
        .Select(g => g.Last())
        .ToList();

    var trajectory = new List<object>();
    ReadinessSnapshot? prevSnap = null;
    foreach (var snap in weeklySnapshots)
    {
        var weekStart = snap.SnapshotDate.AddDays(-(int)snap.SnapshotDate.DayOfWeek + (snap.SnapshotDate.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
        var tradeCount = prevSnap is null ? snap.ScoredClosedTrades : snap.ScoredClosedTrades - prevSnap.ScoredClosedTrades;
        var speed = prevSnap is null ? "Flat"
            : snap.ScoredClosedTrades > prevSnap.ScoredClosedTrades ? "Up"
            : snap.ScoredClosedTrades < prevSnap.ScoredClosedTrades ? "Down" : "Flat";
        trajectory.Add(new { weekStarting = weekStart, tradeCount, winRate = snap.ObservedWinRate, speedIndicator = speed });
        prevSnap = snap;
    }

    var milestones = report.UpcomingMilestones.Select(m => new
    {
        label = m.Title,
        estimatedDateRange = m.EstimatedDateRange,
        completed = m.Status == MilestoneStatus.Completed,
        status = m.Status,
    });

    return Results.Ok(new
    {
        maturityLevel = report.OverallMaturity,
        scoredClosedTrades = report.ScoredClosedTrades,
        observedWinRate = report.WinRate.ObservedRate,
        winRateConfidenceIntervalLow = report.WinRate.ConfidenceLow,
        winRateConfidenceIntervalHigh = report.WinRate.ConfidenceHigh,
        features,
        regimeBullProgress = RegimeProgress(MarketRegime.Bull),
        regimeNeutralProgress = RegimeProgress(MarketRegime.Neutral),
        regimeBearProgress = RegimeProgress(MarketRegime.Bear),
        trajectory,
        milestones,
    });
});

api.MapPost("/readiness/complete-checklist", async (
    CompleteChecklistRequest req,
    ISystemChecklistRepository checklist,
    IAccountContext ctx) =>
{
    await checklist.CompleteAsync(ctx.AccountId, req.CheckName, req.Notes);
    return Results.Ok();
});

static Dictionary<string, decimal> WeightsDict(StrategyWeights w) => new()
{
    ["rsi"] = w.RsiWeight,
    ["macd"] = w.MacdWeight,
    ["volume"] = w.VolumeWeight,
    ["sentiment"] = w.SentimentWeight,
    ["setupQuality"] = w.SetupQualityWeight,
    ["relativeStrength"] = w.RelativeStrengthWeight,
    ["priceLevel"] = w.PriceLevelWeight,
    ["fundamentalMomentum"] = w.FundamentalMomentumWeight,
};

static object MapSuggestion(RefinementSuggestion s)
{
    var currentWeights = JsonSerializer.Deserialize<StrategyWeights>(s.CurrentWeightsJson);
    var suggestedWeights = JsonSerializer.Deserialize<StrategyWeights>(s.SuggestedWeightsJson);
    var findings = JsonSerializer.Deserialize<List<ComponentFinding>>(s.ComponentAnalysisJson) ?? [];

    return new
    {
        id = s.Id,
        generatedAt = s.GeneratedAt,
        analysisPeriodStart = s.AnalysisPeriodStart,
        analysisPeriodEnd = s.AnalysisPeriodEnd,
        tradeCountAnalysed = s.TradeCountAnalysed,
        winnerCount = s.WinnerCount,
        loserCount = s.LoserCount,
        overallWinRate = s.OverallWinRate,
        currentWeights = currentWeights is null ? new() : WeightsDict(currentWeights),
        suggestedWeights = suggestedWeights is null ? new() : WeightsDict(suggestedWeights),
        componentFindings = findings.Select(f => new
        {
            componentName = f.ComponentName,
            currentWeight = f.CurrentWeight,
            winnerAvgScore = f.WinnerAvgScore,
            loserAvgScore = f.LoserAvgScore,
            correlation = f.Correlation,
            suggestedWeight = f.SuggestedWeight,
            weightDelta = f.WeightDelta,
            reasoning = f.Reasoning,
        }),
        assessmentSummary = s.AssessmentSummary,
        confidenceLevel = s.ConfidenceLevel,
        status = s.Status,
        isShadowMode = s.IsShadowMode,
        marketAdjustedWinRate = s.MarketAdjustedWinRate,
        unusualMarketConditions = s.UnusualMarketConditions,
        marketConditionWarning = s.MarketConditionWarning,
    };
}

api.MapGet("/refinement/status", async (
    IStrategyWeightsRepository weightsRepo,
    IRefinementSuggestionRepository suggestionRepo,
    Microsoft.Extensions.Options.IOptions<RefinementConfig> refinementConfig,
    ISignalRepository signalRepo,
    ITradeRepository tradeRepo,
    IAccountRepository accounts,
    IAccountContext ctx,
    CancellationToken ct) =>
{
    var account = await accounts.GetAsync(ctx.AccountId, ct);
    if (account is null) return Results.NotFound();

    var activeWeights = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
    var latest = await suggestionRepo.GetLatestAsync(ctx.AccountId, account.TradingMode);
    var history = (await suggestionRepo.GetHistoryAsync(ctx.AccountId, account.TradingMode)).ToList();

    // Mirrors RefinementService's own sample-size gate (closed trades with a
    // linked, scored signal) so the "progress toward next suggestion" bar
    // reflects the same count that would actually unblock a new one.
    var from = DateTime.UtcNow.AddDays(-refinementConfig.Value.AnalysisPeriodDays);
    var closed = (await tradeRepo.GetTradeHistoryAsync(ctx.AccountId, account.TradingMode, from, DateTime.UtcNow))
        .Where(t => t.Status != TradeStatus.Open && t.SignalId.HasValue);
    var tradesScoredSoFar = 0;
    foreach (var t in closed)
    {
        var signal = await signalRepo.GetByIdAsync(ctx.AccountId, t.SignalId!.Value);
        if (signal?.RsiScore is not null) tradesScoredSoFar++;
    }

    return Results.Ok(new
    {
        currentWeights = activeWeights is null ? new() : WeightsDict(activeWeights),
        latestSuggestion = latest is null ? null : MapSuggestion(latest),
        history = history.Select(MapSuggestion),
        minTradesRequired = refinementConfig.Value.MinCorrelationSampleSize,
        tradesScoredSoFar,
    });
});

api.MapPost("/refinement/apply", async (
    ApplyRefinementRequest req,
    IApplyRefinementService applyService,
    IAccountContext ctx) =>
{
    var result = await applyService.ApplyAsync(ctx.AccountId, req.SuggestionId);
    return result.Success
        ? Results.Ok(new { success = true, message = "Applied" })
        : Results.BadRequest(new { success = false, message = result.Error });
});

api.MapPost("/refinement/reject", async (
    RejectRefinementRequest req,
    IApplyRefinementService applyService,
    IAccountContext ctx) =>
{
    var result = await applyService.RejectAsync(ctx.AccountId, req.SuggestionId, req.Note);
    return result.Success ? Results.Ok() : Results.BadRequest(new { message = result.Error });
});

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
        // Short-lived deliberately: this token is the entire authentication
        // for joining someone's account. A 7-day window meant a leaked/
        // forwarded link (chat scrollback, shared clipboard, proxy logs)
        // stayed exploitable for a week; 30 minutes still comfortably covers
        // "share the link, they click it" while shrinking that exposure a lot.
        ExpiresAt = DateTime.UtcNow.AddMinutes(30),
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
        role = ctx.Role,
    });
});

api.MapGet("/account/me", async (IUserRepository users, IAccountContext ctx) =>
{
    var user = await users.FindAsync(ctx.UserId);
    return Results.Ok(new
    {
        hasConfirmedEmail = user?.HasConfirmedEmail ?? false,
        email = user?.Email ?? string.Empty,
        displayName = user?.DisplayName ?? string.Empty,
    });
});

api.MapPut("/account/me/email", async (
    UpdateMyEmailRequest req,
    IUserRepository users,
    INotificationRecipientRepository recipients,
    IAccountContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var newEmail = req.Email.Trim();
    var user = await users.FindAsync(ctx.UserId);
    var oldEmail = user?.Email;

    await users.UpdateEmailAsync(ctx.UserId, newEmail);

    // Keeps the auto-seeded notification recipient (created at registration
    // with a best-effort, possibly-synthetic email) in sync with the real,
    // user-confirmed one - a no-op if it was never seeded or already differs.
    if (!string.IsNullOrWhiteSpace(oldEmail))
        await recipients.UpdateEmailIfMatchesAsync(ctx.AccountId, oldEmail, newEmail);

    // Only Owners should be auto-seeded into the recipients list — Members
    // joining via invite should never appear there.
    if (ctx.Role == AccountRole.Owner)
    {
        var existingRecipients = await recipients.ListAsync(ctx.AccountId);
        if (!existingRecipients.Any(r => r.Email.Equals(newEmail, StringComparison.OrdinalIgnoreCase)))
        {
            await recipients.AddAsync(new NotificationRecipient
            {
                AccountId = ctx.AccountId,
                Email = newEmail,
                Categories = NotificationCategory.All,
            });
        }
    }

    return Results.Ok();
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

api.MapPut("/account/members/{userId}/approve", async (
    string userId,
    IUserRepository users,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();

    var members = await users.ListByAccountAsync(ctx.AccountId);
    if (!members.Any(m => m.UserId == userId))
        return Results.NotFound();

    await users.ApproveAsync(userId);
    return Results.Ok();
});

// The one path UserRegistrationMiddleware exempts from the pending-approval
// block, so an unapproved user's "waiting for approval" screen has
// something to poll without a 403 loop.
api.MapGet("/account/approval-status", async (IUserRepository users, IAccountContext ctx) =>
{
    var user = await users.FindAsync(ctx.UserId);
    return Results.Ok(new { isApproved = user?.IsApproved ?? false });
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
    IUserRepository users,
    [FromServices] ServiceBusClient? serviceBus,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();
    if (!ApiKeyProviders.All.Contains(provider))
        return Results.BadRequest(new { message = $"Unknown provider '{provider}'." });
    if (string.IsNullOrWhiteSpace(req.Value))
        return Results.BadRequest(new { message = "Value cannot be empty." });

    await keys.SaveKeyAsync(ctx.AccountId, provider, req.Value);
    var isValid = await keys.TestKeyAsync(ctx.AccountId, provider);

    // The moment a user's keys satisfy onboarding for the first time, kick
    // off an immediate Watchlist run rather than making them wait until the
    // next Sunday 20:00 ET schedule - IsOnboarded is otherwise unused, so it
    // doubles as the "have we already fired this" guard.
    var user = await users.FindAsync(ctx.UserId);
    if (user is { IsOnboarded: false } && serviceBus is not null)
    {
        var statuses = await keys.GetKeyStatusesAsync(ctx.AccountId);
        if (IsReallyOnboarded(statuses))
        {
            await users.MarkOnboardedAsync(ctx.UserId);
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York"));
            await using var sender = serviceBus.CreateSender("watchlist-jobs");
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(
                new WatchlistJobMessage(ctx.AccountId, Guid.NewGuid().ToString("N"), nowEt))));
        }
    }

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
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();
    await keys.DeleteKeyAsync(ctx.AccountId, provider);
    return Results.Ok();
});

// Trading config, notifications, and account lifecycle (Phase 10d Settings page)
api.MapPut("/account/trading-config", async (
    UpdateTradingConfigRequest req,
    IAccountRepository accounts,
    ITradeRepository trades,
    IUserKeyService keys,
    IConfiguration configuration,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();

    var account = await accounts.GetAsync(ctx.AccountId)
        ?? throw new InvalidOperationException("Authenticated caller has no account.");

    // Same admin check the "Admin" authorization policy uses (AdminHandler):
    // the single configured Admin:UserId matched against the "sub" claim.
    var adminUserId = configuration["Admin:UserId"];
    var isAdmin = !string.IsNullOrEmpty(adminUserId) && ctx.UserId == adminUserId;

    // Trading212 issues separate credentials per environment - switching
    // TradingMode without the matching pair saved would just break every
    // T212 call the account makes, so block the switch until that pair exists.
    if (req.TradingMode != account.TradingMode)
    {
        var (keyProvider, secretProvider) = req.TradingMode == TradingMode.Live
            ? (ApiKeyProviders.Trading212LiveKey, ApiKeyProviders.Trading212LiveSecret)
            : (ApiKeyProviders.Trading212DemoKey, ApiKeyProviders.Trading212DemoSecret);

        var statuses = await keys.GetKeyStatusesAsync(ctx.AccountId);
        var hasPair = statuses.GetValueOrDefault(keyProvider) != KeyStatus.NotSet
            && statuses.GetValueOrDefault(secretProvider) != KeyStatus.NotSet;

        if (!hasPair)
            return Results.BadRequest(new
            {
                message = $"Add your Trading212 {req.TradingMode} API key and secret in Settings before switching to {req.TradingMode} mode.",
            });

        // Monitor only watches trades matching the account's *current* mode,
        // so switching away with positions still open would silently orphan
        // them - no stop-loss, target, trailing-stop, or time-exit
        // enforcement until switching back. For Live positions that means
        // real money sitting unprotected. Normally a hard block: the
        // positions must be closed first. An admin can force past it for
        // testing (canForce below tells the frontend to offer the override),
        // which knowingly leaves those positions unmonitored.
        var openInCurrentMode = (await trades.GetOpenTradesAsync(ctx.AccountId, account.TradingMode)).ToList();
        if (openInCurrentMode.Count > 0 && !(isAdmin && req.Force))
            return Results.BadRequest(new
            {
                message = $"You have {openInCurrentMode.Count} open {account.TradingMode} position(s) " +
                    $"({string.Join(", ", openInCurrentMode.Select(t => t.Symbol))}). " +
                    $"SwingTrader stops monitoring them the moment you switch modes — close them first.",
                canForce = isAdmin,
            });
    }

    account.TradingMode = req.TradingMode;
    account.ApprovalRequired = req.ApprovalRequired;
    await accounts.UpdateAsync(account);
    return Results.Ok();
});

// Manual weight/threshold tuning (e.g. temporarily lowering BuyThreshold to
// exercise the Execution path on demo data) - an in-place edit of the active
// row, not a Refinement-style versioned suggestion.
api.MapGet("/strategy-weights", async (IStrategyWeightsRepository weightsRepo, IAccountContext ctx) =>
{
    var active = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
    return active is null ? Results.NotFound() : Results.Ok(active);
});

api.MapPut("/strategy-weights", async (
    UpdateStrategyWeightsRequest req,
    IStrategyWeightsRepository weightsRepo,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();

    try
    {
        await weightsRepo.UpdateWeightsAsync(ctx.AccountId, new StrategyWeightsUpdate(
            req.RsiWeight, req.MacdWeight, req.VolumeWeight, req.SentimentWeight,
            req.SetupQualityWeight, req.RelativeStrengthWeight, req.PriceLevelWeight,
            req.FundamentalMomentumWeight, req.BuyThreshold, req.WatchThreshold, req.StopLossPctDefault));
        return Results.Ok();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

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
        profile.RiskLabel,
        BuyThreshold = weights?.BuyThreshold,
        WatchThreshold = weights?.WatchThreshold,
        StopLossPctDefault = weights?.StopLossPctDefault,
        CapitalBreakdown = snapshot is null ? null : new
        {
            TotalCapital = snapshot.TotalCapital,
            LockedCapital = snapshot.TotalCapital * profile.LockedCapitalPct,
            ActiveCapital = snapshot.ActiveCapital,
            MaxPerTrade = snapshot.ActiveCapital * profile.MaxPositionPctOfActive,
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

// Multiple named watchlists — see IWatchlistRepository for the caps
// (WatchlistLimits.MaxEnabledWatchlists / MaxSymbolsPerWatchlist) enforced
// server-side regardless of what the UI lets through.
var watchlistsGroup = api.MapGroup("/watchlists");

watchlistsGroup.MapGet("/", async (IWatchlistRepository watchlists, IAccountContext ctx) =>
    Results.Ok(await watchlists.GetAllWatchlistsAsync(ctx.AccountId)));

watchlistsGroup.MapGet("/enabled-symbols", async (IWatchlistRepository watchlists, IAccountContext ctx) =>
    Results.Ok(await watchlists.GetAllEnabledSymbolsAsync(ctx.AccountId)));

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
}).RequireRateLimiting(ExternalReadPolicy);

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

api.MapPut("/account/global-refinement-optin/{enabled:bool}", async (
    bool enabled,
    IAccountRepository accounts,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner) return Results.Forbid();
    var account = await accounts.GetAsync(ctx.AccountId)
        ?? throw new InvalidOperationException("Authenticated caller has no account.");
    account.GlobalRefinementOptIn = enabled;
    await accounts.UpdateAsync(account);
    return Results.Ok();
});

api.MapGet("/account/notifications", async (INotificationRecipientRepository recipients, IAccountContext ctx) =>
{
    var list = await recipients.ListAsync(ctx.AccountId);
    // Categories serializes as a comma-separated flag-name string (the global
    // JsonStringEnumConverter's default [Flags] behaviour) - expose a plain
    // computed bool instead of making the client parse that string.
    return Results.Ok(list.Select(r => new
    {
        r.Id,
        r.Email,
        TradeApprovalEnabled = r.Categories.HasFlag(NotificationCategory.TradeApproval),
    }));
});

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
    {
        var all = await recipients.ListAsync(ctx.AccountId);
        var target = all.FirstOrDefault(r => r.Id == recipientId);
        if (target is null || !target.Email.Equals(ctx.Email, StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();
    }

    await recipients.RemoveAsync(ctx.AccountId, recipientId);
    return Results.Ok();
});

api.MapPut("/account/notifications/{recipientId:int}/trade-approval", async (
    int recipientId,
    SetTradeApprovalRequest req,
    INotificationRecipientRepository recipients,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
    {
        var all = await recipients.ListAsync(ctx.AccountId);
        var target = all.FirstOrDefault(r => r.Id == recipientId);
        if (target is null || !target.Email.Equals(ctx.Email, StringComparison.OrdinalIgnoreCase))
            return Results.Forbid();
    }

    var updated = await recipients.SetTradeApprovalAsync(ctx.AccountId, recipientId, req.Enabled);
    return updated ? Results.Ok() : Results.NotFound();
});

// Soft-delete only - see Account.IsDeleted for why a hard delete isn't
// feasible without cascading through every scoped table.
api.MapDelete("/account", async (IAccountRepository accounts, IUserRepository users, IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();

    var account = await accounts.GetAsync(ctx.AccountId)
        ?? throw new InvalidOperationException("Authenticated caller has no account.");
    account.IsDeleted = true;
    await accounts.UpdateAsync(account);

    // Also remove every AppUser tied to this Account (Owner and any
    // Members) - otherwise their UserId row survives with AccountId
    // pointing at a now-deleted Account, and UserRegistrationMiddleware
    // permanently 403s them on every future login instead of letting them
    // re-register fresh.
    foreach (var member in await users.ListByAccountAsync(ctx.AccountId))
        await users.RemoveAsync(member.UserId);

    return Results.Ok();
});

// Manual "run now" triggers for the dashboard's per-job buttons. Sends
// straight onto the same Service Bus queue the Scheduler enqueues onto, so
// the existing Consumer Functions pick it up identically - but deliberately
// skips the JobLog per-day idempotency row the Scheduler writes, so a manual
// run can't collide with (or get silently swallowed by) an automatic run
// that already fired today, and testing can re-trigger a job repeatedly in
// one day. The Consumer Functions no-op their JobLog Mark* calls when no
// matching row exists, so this is safe.
var runGroup = app.MapGroup("/run").RequireAuthorization().RequireRateLimiting(ClaudeJobsPolicy);
runGroup.MapPost("/{jobType}", async (
    string jobType,
    [FromServices] ServiceBusClient? serviceBus,
    IAccountContext ctx) =>
{
    if (ctx.Role != AccountRole.Owner)
        return Results.Forbid();
    if (serviceBus is null)
        return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York"));
    var today = DateOnly.FromDateTime(nowEt);
    var jobId = Guid.NewGuid().ToString("N");

    (string QueueName, object Message)? job = jobType.ToLowerInvariant() switch
    {
        "research" => ("research-jobs", new ResearchJobMessage(ctx.AccountId, jobId, today, nowEt)),
        "watchlist" => ("watchlist-jobs", new WatchlistJobMessage(ctx.AccountId, jobId, nowEt)),
        "report" => ("report-jobs", new ReportJobMessage(ctx.AccountId, jobId, today)),
        "execution" => ("execution-jobs", new ExecutionJobMessage(ctx.AccountId, jobId, today)),
        "monitor" => ("monitor-jobs", new MonitorJobMessage(ctx.AccountId, jobId, nowEt)),
        "risk" => ("risk-jobs", new RiskJobMessage(ctx.AccountId, jobId, today)),
        "refinement" => ("refinement-jobs", new RefinementJobMessage(ctx.AccountId, jobId, today)),
        _ => null,
    };

    if (job is null)
        return Results.BadRequest(new { message = $"Unknown job type '{jobType}'." });

    await using var sender = serviceBus.CreateSender(job.Value.QueueName);
    await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(job.Value.Message)));

    return Results.Ok(new { jobId, jobType, queuedAt = nowEt });
});

// Admin area — global across all accounts, gated by Admin:UserId (a single
// B2C object ID), a separate concept from AccountRole.Owner. SendMessage is
// deliberately not implemented in this pass (no in-app messaging channel
// exists yet - deferred rather than half-built as an endpoint with nowhere
// to deliver to).
var adminGroup = app.MapGroup("/api/admin").RequireAuthorization("Admin");

adminGroup.MapGet("/me", () => Results.Ok(new { isAdmin = true }));

// "Onboarding complete" is computed from key statuses (see
// onboarding.guard.ts's isOnboardingComplete) rather than trusted from the
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

// DB column alone. Claude isn't required here - it has a shared fallback
// key (see UserKeyService.GetKeyAsync), so accounts never need their own.
static bool IsReallyOnboarded(Dictionary<string, KeyStatus> statuses)
{
    bool HasPair(string keyProvider, string secretProvider) =>
        statuses.GetValueOrDefault(keyProvider) != KeyStatus.NotSet && statuses.GetValueOrDefault(secretProvider) != KeyStatus.NotSet;

    var hasCoreKeys = statuses.GetValueOrDefault(ApiKeyProviders.Finnhub) != KeyStatus.NotSet
        && statuses.GetValueOrDefault(ApiKeyProviders.Tiingo) != KeyStatus.NotSet;
    var hasTrading212Pair = HasPair(ApiKeyProviders.Trading212DemoKey, ApiKeyProviders.Trading212DemoSecret)
        || HasPair(ApiKeyProviders.Trading212LiveKey, ApiKeyProviders.Trading212LiveSecret);

    return hasCoreKeys && hasTrading212Pair;
}

adminGroup.MapGet("/stats", async (IAdminRepository admin, IUserKeyService keys, CancellationToken ct) =>
{
    var stats = await admin.GetStatsAsync(ct);
    var users = await admin.GetUsersAsync(ct);

    var notOnboarded = 0;
    foreach (var user in users)
        if (!IsReallyOnboarded(await keys.GetKeyStatusesAsync(user.AccountId, ct)))
            notOnboarded++;

    return Results.Ok(stats with { UsersNotOnboarded = notOnboarded });
});

adminGroup.MapGet("/users", async (IAdminRepository admin, IUserKeyService keys, CancellationToken ct) =>
{
    var users = await admin.GetUsersAsync(ct);
    var withRealOnboarding = new List<AdminUserSummary>(users.Count);
    foreach (var user in users)
        withRealOnboarding.Add(user with { IsOnboarded = IsReallyOnboarded(await keys.GetKeyStatusesAsync(user.AccountId, ct)) });

    return Results.Ok(withRealOnboarding);
});

adminGroup.MapGet("/users/{userId}", async (string userId, IAdminRepository admin, IUserKeyService keys, CancellationToken ct) =>
{
    var user = await admin.GetUserAsync(userId, ct);
    if (user is null) return Results.NotFound();

    var isOnboarded = IsReallyOnboarded(await keys.GetKeyStatusesAsync(user.AccountId, ct));
    return Results.Ok(user with { IsOnboarded = isOnboarded });
});

static string AdminId(HttpContext context) => context.User.FindFirst("sub")?.Value ?? "unknown";

adminGroup.MapPost("/users/{userId}/suspend", async (
    string userId,
    SuspendUserRequest req,
    IUserRepository users,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    await users.SuspendAsync(userId, req.Reason, ct);
    await adminLog.LogAsync(new AdminActionLog
    {
        AdminUserId = AdminId(http),
        TargetUserId = userId,
        Action = "Suspend",
        Details = req.Reason is null ? null : $"Reason: {req.Reason}",
    }, ct);
    return Results.Ok();
});

adminGroup.MapPost("/users/{userId}/unsuspend", async (
    string userId,
    IUserRepository users,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    await users.UnsuspendAsync(userId, ct);
    await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "Unsuspend" }, ct);
    return Results.Ok();
});

adminGroup.MapPost("/users/{userId}/reset-onboarding", async (
    string userId,
    IUserRepository users,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    await users.ResetOnboardingAsync(userId, ct);
    await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "ResetOnboarding" }, ct);
    return Results.Ok();
});

adminGroup.MapPost("/users/{userId}/force-demo", async (
    string userId,
    IUserRepository users,
    IAccountRepository accounts,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    var user = await users.FindAsync(userId, ct);
    if (user?.AccountId is null) return Results.NotFound();

    var account = await accounts.GetAsync(user.AccountId.Value);
    if (account is null) return Results.NotFound();

    account.TradingMode = TradingMode.Demo;
    await accounts.UpdateAsync(account, ct);
    await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "ForceDemo" }, ct);
    return Results.Ok();
});

adminGroup.MapDelete("/users/{userId}", async (
    string userId,
    IUserRepository users,
    IAccountRepository accounts,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    var user = await users.FindAsync(userId, ct);
    if (user is null) return Results.NotFound();

    if (user.Role == AccountRole.Owner && user.AccountId is not null)
    {
        // Soft-delete the whole Account, matching the self-service delete
        // path - an Owner's Account is theirs, not just their AppUser row.
        // Also remove every AppUser tied to it (Owner and any Members) -
        // otherwise their UserId row survives with AccountId pointing at a
        // now-deleted Account, and UserRegistrationMiddleware permanently
        // 403s them on every future login instead of letting them
        // re-register fresh, since it only creates a new Account when no
        // AppUser row exists yet for that identity.
        var account = await accounts.GetAsync(user.AccountId.Value);
        if (account is not null)
        {
            account.IsDeleted = true;
            await accounts.UpdateAsync(account, ct);
        }

        foreach (var member in await users.ListByAccountAsync(user.AccountId.Value, ct))
            await users.RemoveAsync(member.UserId, ct);
    }
    else
    {
        await users.RemoveAsync(userId, ct);
    }

    await adminLog.LogAsync(new AdminActionLog { AdminUserId = AdminId(http), TargetUserId = userId, Action = "DeleteUser" }, ct);
    return Results.Ok();
});

adminGroup.MapGet("/jobs/failures", async (IAdminRepository admin, CancellationToken ct) =>
    Results.Ok(await admin.GetJobFailuresAsync(TimeSpan.FromHours(48), ct)));

adminGroup.MapPost("/jobs/retry", async (
    RetryJobRequest req,
    IAdminRepository admin,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    var retried = await admin.RetryJobAsync(req.JobLogId, ct);
    if (!retried) return Results.NotFound(new { message = "Job not found or not in a failed state." });

    await adminLog.LogAsync(new AdminActionLog
    {
        AdminUserId = AdminId(http),
        TargetUserId = "system",
        Action = "RetryJob",
        Details = $"JobLogId: {req.JobLogId}",
    }, ct);
    return Results.Ok();
});

adminGroup.MapDelete("/jobs/{jobLogId:int}", async (
    int jobLogId,
    IAdminRepository admin,
    IAdminLogRepository adminLog,
    HttpContext http,
    CancellationToken ct) =>
{
    var deleted = await admin.DeleteJobFailureAsync(jobLogId, ct);
    if (!deleted) return Results.NotFound(new { message = "Job not found or not in a failed state." });

    await adminLog.LogAsync(new AdminActionLog
    {
        AdminUserId = AdminId(http),
        TargetUserId = "system",
        Action = "DeleteJobFailure",
        Details = $"JobLogId: {jobLogId}",
    }, ct);
    return Results.Ok();
});

adminGroup.MapGet("/logs", async (IAdminLogRepository adminLog, CancellationToken ct) =>
    Results.Ok(await adminLog.GetRecentAsync(200, ct)));

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
