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
using Serilog;
using SwingTrader.Core.Interfaces;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Security;

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

Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Services.AddSerilog();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
