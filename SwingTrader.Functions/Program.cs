using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using Refit;
using Serilog;
using SwingTrader.Agents.Execution;
using SwingTrader.Agents.Monitor;
using SwingTrader.Agents.Refinement;
using SwingTrader.Agents.Report;
using SwingTrader.Agents.Risk;
using SwingTrader.Agents.Research;
using SwingTrader.Agents.Watchlist;
using SwingTrader.Core.Interfaces;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Fundamental;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Security;
using SwingTrader.Infrastructure.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Configuration.AddEnvironmentVariables();
var keyVaultUrl = builder.Configuration["KeyVaultUrl"];
if (!string.IsNullOrEmpty(keyVaultUrl))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUrl),
        new DefaultAzureCredential());
}

builder.Services.AddDbContext<SwingTraderDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IJobLogRepository, JobLogRepository>();
builder.Services.AddScoped<IUserApiKeyRepository, UserApiKeyRepository>();
builder.Services.AddScoped<IKeyEncryptionService, KeyEncryptionService>();
builder.Services.AddScoped<IUserKeyService, UserKeyService>();
builder.Services.AddScoped<IUserHttpClientFactory, UserHttpClientFactory>();
builder.Services.AddScoped<IWatchlistRepository, WatchlistRepository>();
builder.Services.AddScoped<IStrategyWeightsRepository, StrategyWeightsRepository>();
builder.Services.AddScoped<IAccountRiskProfileRepository, AccountRiskProfileRepository>();
builder.Services.AddScoped<ICandleRepository, CandleRepository>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<IPortfolioRepository, PortfolioRepository>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IApprovalRepository, ApprovalRepository>();
builder.Services.AddScoped<IWatchlistHistoryRepository, WatchlistHistoryRepository>();
builder.Services.AddScoped<IWorkerHeartbeatRepository, WorkerHeartbeatRepository>();
builder.Services.AddScoped<IRefinementSuggestionRepository, RefinementSuggestionRepository>();
builder.Services.AddScoped<ITierEvaluationRepository, TierEvaluationRepository>();
builder.Services.AddScoped<IReadinessSnapshotRepository, ReadinessSnapshotRepository>();
builder.Services.AddScoped<ISystemChecklistRepository, SystemChecklistRepository>();
builder.Services.AddScoped<INotificationRecipientRepository, NotificationRecipientRepository>();

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRateLimiter>(_ => new RateLimiter(maxCallsPerMinute: 50));
builder.Services.Configure<ClaudeConfig>(builder.Configuration.GetSection(ClaudeConfig.SectionName));
builder.Services.Configure<ResearchConfig>(builder.Configuration.GetSection(ResearchConfig.SectionName));
builder.Services.Configure<EarningsConfig>(builder.Configuration.GetSection(EarningsConfig.SectionName));
builder.Services.Configure<FundamentalConfig>(builder.Configuration.GetSection(FundamentalConfig.SectionName));
builder.Services.Configure<PriceLevelConfig>(builder.Configuration.GetSection(PriceLevelConfig.SectionName));
builder.Services.Configure<WatchlistConfig>(builder.Configuration.GetSection(WatchlistConfig.SectionName));
builder.Services.Configure<ReportConfig>(builder.Configuration.GetSection(ReportConfig.SectionName));
builder.Services.Configure<ApprovalConfig>(builder.Configuration.GetSection(ApprovalConfig.SectionName));
builder.Services.Configure<EmailConfig>(builder.Configuration.GetSection(EmailConfig.SectionName));
builder.Services.Configure<MonitorConfig>(builder.Configuration.GetSection(MonitorConfig.SectionName));
builder.Services.Configure<RefinementConfig>(builder.Configuration.GetSection(RefinementConfig.SectionName));
builder.Services.Configure<RiskManagementConfig>(builder.Configuration.GetSection(RiskManagementConfig.SectionName));
builder.Services.Configure<ExecutionConfig>(builder.Configuration.GetSection(ExecutionConfig.SectionName));

builder.Services.AddRefitClient<IExchangeRateClient>()
    .ConfigureHttpClient(c => c.BaseAddress = new Uri("https://api.frankfurter.dev"));

builder.Services.AddSingleton<IIndicatorService, IndicatorService>();
builder.Services.AddScoped<IMarketDataService, MarketDataService>();
builder.Services.AddScoped<IMarketCalendarService, MarketCalendarService>();
builder.Services.AddScoped<IForexService, ForexService>();
builder.Services.AddScoped<IEarningsService, EarningsService>();
builder.Services.AddScoped<IRelativeStrengthService, RelativeStrengthService>();
builder.Services.AddScoped<IPriceLevelService, PriceLevelService>();
builder.Services.AddScoped<IMarketRegimeService, MarketRegimeService>();
builder.Services.AddScoped<IMarketUniverseService, MarketUniverseService>();

// Wikipedia requires a descriptive User-Agent identifying the app/contact
// per their bot policy - requests without one can get blocked outright.
builder.Services.AddHttpClient<IWikipediaIndexClient, WikipediaIndexClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SwingTraderBot/1.0 (personal swing-trading app; contact via GitHub repo)");
});
builder.Services.AddScoped<IFundamentalDataService, FundamentalDataService>();
builder.Services.AddScoped<IFundamentalScoringService, FundamentalScoringService>();
builder.Services.AddScoped<IResearchPipeline, ResearchPipeline>();
builder.Services.AddScoped<IStockScreener, StockScreener>();
builder.Services.AddScoped<IWatchlistSelectionService, WatchlistSelectionService>();
builder.Services.AddScoped<IWatchlistUpdateService, WatchlistUpdateService>();
builder.Services.AddScoped<IReportGenerationService, ReportGenerationService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPositionMonitorService, PositionMonitorService>();
builder.Services.AddScoped<IPortfolioCircuitBreakerService, PortfolioCircuitBreakerService>();
builder.Services.AddScoped<IMonitorService, MonitorService>();
builder.Services.AddScoped<IComponentCorrelationService, ComponentCorrelationService>();
builder.Services.AddScoped<IRefinementService, RefinementService>();
builder.Services.AddScoped<IApplyRefinementService, ApplyRefinementService>();
builder.Services.AddScoped<ITierEvaluationService, TierEvaluationService>();
builder.Services.AddScoped<IPositionSizingService, PositionSizingService>();
builder.Services.AddScoped<IExecutionService, ExecutionService>();

// Managed-identity Service Bus client (ServiceBusConnection__fullyQualifiedNamespace
// env var, set by Bicep) - the Scheduler sends via this; each Consumer's
// [ServiceBusTrigger(Connection = "ServiceBusConnection")] uses the same
// setting to receive.
var serviceBusNamespace = builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"];
if (!string.IsNullOrEmpty(serviceBusNamespace))
{
    builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential()));
}

// Same DI registrations as the API (minus HTTP endpoints) land here once
// SwingTrader.Agents/Infrastructure business logic is ported.

// ReadFrom.Configuration alone produces zero sinks without a "Serilog"
// appsettings.json section (which this project has never had) - every
// LogInformation/LogWarning/LogError call across every agent silently went
// nowhere, not even Console, which is exactly what made a real Trading212
// API failure inside MonitorService.UpdateSnapshotAsync's try/catch
// undiagnosable. Console always gets wired so `func azure functionapp
// logstream` / Kudu show something. (Serilog.Sinks.ApplicationInsights
// isn't used here - it pins Microsoft.ApplicationInsights <3.0, which
// conflicts with Microsoft.ApplicationInsights.WorkerService 3.1.2 this
// project already needs; the OpenTelemetry Azure Monitor exporter below
// covers request/dependency telemetry instead.)
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();
builder.Services.AddSerilog();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
