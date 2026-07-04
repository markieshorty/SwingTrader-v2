using Azure.Identity;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using SwingTrader.Api.HealthChecks;
using SwingTrader.Data;

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

// Health endpoints
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

// Placeholder — full API surface (/api/status, /api/portfolio, /api/positions,
// /api/signals/today, /api/trades/recent, /api/watchlist, /api/refinement/*,
// /api/readiness, /approve, /run/*) lands once SwingTrader.Agents and
// SwingTrader.Infrastructure business logic is ported in later phases.
app.MapGet("/api/status", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

// Angular static files (Phase 10b populates wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
