# SwingTrader — Phase 10a
# Azure Infrastructure, CI/CD, and Solution Structure

## What This Phase Builds

Clean mono-repo solution structure.
All Azure infrastructure via Bicep IaC.
GitHub Actions CI/CD pipeline.
Azure SQL replacing SQLite.
ASP.NET Core API and Azure Functions projects.
No manual Azure CLI commands after
the one-time OIDC bootstrap.

## Estimated Cost (Single User)

```
Azure Container Apps (API): £0/month
  Scale to zero, within free grant
Azure Function App:          £0/month
  Consumption plan, within free tier
Azure SQL Basic:             £4/month
Azure Key Vault Standard:    £3/month
Azure Container Registry:    £4/month
Azure App Insights:          £0/month
  Within 5GB free tier
UptimeRobot (keep-alive):    £0/month
─────────────────────────────────────
Total:                      ~£11/month
```

---

## Step 1: Solution Structure

Create a new blank solution:

```
dotnet new sln --name SwingTrader
```

Create projects:

```bash
# Core domain — entities, interfaces, enums
dotnet new classlib -n SwingTrader.Core
dotnet sln add SwingTrader.Core

# Data — EF Core, DbContext, repositories, migrations
dotnet new classlib -n SwingTrader.Data
dotnet sln add SwingTrader.Data

# Agents — all business logic services and pipelines
dotnet new classlib -n SwingTrader.Agents
dotnet sln add SwingTrader.Agents

# Infrastructure — HTTP clients, email, external APIs
dotnet new classlib -n SwingTrader.Infrastructure
dotnet sln add SwingTrader.Infrastructure

# API — ASP.NET Core, serves Angular, all HTTP endpoints
dotnet new webapi -n SwingTrader.Api
dotnet sln add SwingTrader.Api

# Functions — Azure Functions, all scheduled workers
dotnet new func --worker-runtime dotnet-isolated \
  -n SwingTrader.Functions
dotnet sln add SwingTrader.Functions

# Tests — xUnit test project
dotnet new xunit -n SwingTrader.Tests
dotnet sln add SwingTrader.Tests
```

Project references:

```bash
# Data references Core
dotnet add SwingTrader.Data reference SwingTrader.Core

# Agents references Core, Data, Infrastructure
dotnet add SwingTrader.Agents reference SwingTrader.Core
dotnet add SwingTrader.Agents reference SwingTrader.Data
dotnet add SwingTrader.Agents reference SwingTrader.Infrastructure

# Infrastructure references Core
dotnet add SwingTrader.Infrastructure reference SwingTrader.Core

# Api references Agents and Data
dotnet add SwingTrader.Api reference SwingTrader.Agents
dotnet add SwingTrader.Api reference SwingTrader.Data

# Functions references Agents and Data
dotnet add SwingTrader.Functions reference SwingTrader.Agents
dotnet add SwingTrader.Functions reference SwingTrader.Data

# Tests references all
dotnet add SwingTrader.Tests reference SwingTrader.Core
dotnet add SwingTrader.Tests reference SwingTrader.Data
dotnet add SwingTrader.Tests reference SwingTrader.Agents
dotnet add SwingTrader.Tests reference SwingTrader.Infrastructure
```

Full folder structure after this step:

```
SwingTrader/
├── .github/
│   └── workflows/         ← created in Step 5
├── infra/                 ← created in Step 4
├── SwingTrader.Core/
├── SwingTrader.Data/
├── SwingTrader.Agents/
├── SwingTrader.Infrastructure/
├── SwingTrader.Api/
│   └── Dockerfile         ← created in Step 3
├── SwingTrader.Functions/
├── SwingTrader.Tests/
├── SwingTrader.Angular/   ← Phase 10b
└── SwingTrader.sln
```

---

## Step 2: NuGet Packages

### SwingTrader.Core

```bash
dotnet add SwingTrader.Core package Microsoft.Extensions.DependencyInjection.Abstractions
```

### SwingTrader.Data

```bash
dotnet add SwingTrader.Data package Microsoft.EntityFrameworkCore.SqlServer --version 9.*
dotnet add SwingTrader.Data package Microsoft.EntityFrameworkCore.Tools --version 9.*
dotnet add SwingTrader.Data package Microsoft.EntityFrameworkCore.Design --version 9.*
```

### SwingTrader.Agents

```bash
dotnet add SwingTrader.Agents package Microsoft.Extensions.Http
dotnet add SwingTrader.Agents package Markdig
dotnet add SwingTrader.Agents package MathNet.Numerics
```

### SwingTrader.Infrastructure

```bash
dotnet add SwingTrader.Infrastructure package Refit
dotnet add SwingTrader.Infrastructure package Refit.HttpClientFactory
dotnet add SwingTrader.Infrastructure package System.Net.Mail
```

### SwingTrader.Api

```bash
dotnet add SwingTrader.Api package Microsoft.EntityFrameworkCore.SqlServer --version 9.*
dotnet add SwingTrader.Api package Azure.Identity
dotnet add SwingTrader.Api package Azure.Extensions.AspNetCore.Configuration.Secrets
dotnet add SwingTrader.Api package Microsoft.Extensions.Azure
dotnet add SwingTrader.Api package Microsoft.ApplicationInsights.AspNetCore
dotnet add SwingTrader.Api package Serilog.AspNetCore
dotnet add SwingTrader.Api package Serilog.Sinks.ApplicationInsights
dotnet add SwingTrader.Api package AspNetCore.HealthChecks.UI.Client
dotnet add SwingTrader.Api package Swashbuckle.AspNetCore
```

### SwingTrader.Functions

```bash
dotnet add SwingTrader.Functions package Microsoft.Azure.Functions.Worker --version 1.*
dotnet add SwingTrader.Functions package Microsoft.Azure.Functions.Worker.Extensions.Timer --version 4.*
dotnet add SwingTrader.Functions package Microsoft.EntityFrameworkCore.SqlServer --version 9.*
dotnet add SwingTrader.Functions package Azure.Identity
dotnet add SwingTrader.Functions package Azure.Extensions.AspNetCore.Configuration.Secrets
dotnet add SwingTrader.Functions package Microsoft.ApplicationInsights.WorkerService
dotnet add SwingTrader.Functions package Serilog.Extensions.Hosting
```

### SwingTrader.Tests

```bash
dotnet add SwingTrader.Tests package xunit
dotnet add SwingTrader.Tests package xunit.runner.visualstudio
dotnet add SwingTrader.Tests package Microsoft.NET.Test.Sdk
dotnet add SwingTrader.Tests package Microsoft.EntityFrameworkCore.InMemory
dotnet add SwingTrader.Tests package NSubstitute
dotnet add SwingTrader.Tests package FluentAssertions
```

---

## Step 3: Core Implementation

### Domain Entities (SwingTrader.Core)

Reference the legacy repo for all entity
definitions. The following entities are
required at minimum for Phase 10a:

Entities to implement (copy from legacy):
  Trade
  StockSignal
  StockCandle
  WatchlistItem
  StrategyWeights
  DailyReport
  PortfolioSnapshot
  WorkerHeartbeat
  TradeApproval
  WatchlistHistory
  RefinementSuggestion
  TierEvaluationRecord
  SystemChecklist
  ReadinessSnapshot

Enums to implement:
  TradeStatus
  SetupType
  MarketRegime
  AnalystTrend
  InsiderActivity
  EarningsConsistency
  RevenueDirection
  DataMaturityLevel
  ReadinessStatus
  FeatureRiskLevel
  WatchlistAction
  JobStatus

BaseEntity:
  int Id
  DateTime CreatedAt
  DateTime UpdatedAt

All entities inherit BaseEntity.
No UserId yet — that is Phase 10c.

### EF Core DbContext (SwingTrader.Data)

Reference legacy SwingTrader.Data/
SwingTraderDbContext.cs for:
  All DbSet<T> definitions
  OnModelCreating configuration
    (indexes, relationships, constraints)
  Global datetime2 convention
  Seed data for StrategyWeights

Use SQL Server provider:
  options.UseSqlServer(connectionString)

### Migrations

After implementing all entities and DbContext:

```bash
dotnet ef migrations add InitialSchema \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

Do not run Update-Database manually.
Migrations apply via CI/CD (Step 5)
and on API startup (Program.cs).

### SwingTrader.Api/Program.cs

Reference legacy SwingTrader.Host/Program.cs.

Key sections to implement:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Key Vault (production only)
var keyVaultUrl = builder.Configuration["KeyVaultUrl"];
if (!string.IsNullOrEmpty(keyVaultUrl))
{
  builder.Configuration.AddAzureKeyVault(
    new Uri(keyVaultUrl),
    new DefaultAzureCredential());
}

// Serilog
builder.Host.UseSerilog((ctx, cfg) =>
{
  cfg.ReadFrom.Configuration(ctx.Configuration);
  var aiConnectionString = ctx.Configuration
    ["ApplicationInsights:ConnectionString"];
  if (!string.IsNullOrEmpty(aiConnectionString))
    cfg.WriteTo.ApplicationInsights(
      aiConnectionString,
      TelemetryConverter.Traces);
});

// EF Core with SQL Server
builder.Services.AddDbContext<SwingTraderDbContext>(
  options => options.UseSqlServer(
    builder.Configuration
      .GetConnectionString("DefaultConnection")));

// All DI registrations
// (repositories, services, HTTP clients, configs)
// Reference legacy Program.cs for full list

// Health checks
builder.Services.AddHealthChecks()
  .AddCheck<DatabaseHealthCheck>("database",
    tags: new[] { "ready" })
  .AddCheck<TradingApiHealthCheck>("trading212",
    tags: new[] { "ready", "external" })
  .AddCheck<FinnhubHealthCheck>("finnhub",
    tags: new[] { "ready", "external" })
  .AddCheck<ClaudeHealthCheck>("claude",
    tags: new[] { "ready", "external" })
  .AddCheck<WorkerHealthCheck>("workers",
    tags: new[] { "live" });

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS for Angular dev server
builder.Services.AddCors(options =>
{
  options.AddPolicy("Angular", policy =>
    policy.WithOrigins("http://localhost:4200")
      .AllowAnyHeader()
      .AllowAnyMethod());
});

var app = builder.Build();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
  var db = scope.ServiceProvider
    .GetRequiredService<SwingTraderDbContext>();
  await db.Database.MigrateAsync();
}

app.UseSerilogRequestLogging();
app.UseCors("Angular");

// Health endpoints
app.MapHealthChecks("/health",
  new HealthCheckOptions {
    ResponseWriter =
      UIResponseWriter.WriteHealthCheckUIResponse
  });
app.MapHealthChecks("/health/ready",
  new HealthCheckOptions {
    Predicate = c => c.Tags.Contains("ready")
  });
app.MapHealthChecks("/health/live",
  new HealthCheckOptions {
    Predicate = c => c.Tags.Contains("live")
  });

// Swagger (dev only)
if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI();
}

// All API endpoints
// Reference legacy Program.cs endpoint mappings
// /api/status, /api/portfolio, /api/positions
// /api/signals/today, /api/trades/recent
// /api/watchlist, /api/refinement/*
// /api/readiness, /approve, /run/*

// Angular static files (Phase 10b populates wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
```

### SwingTrader.Api/Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["SwingTrader.Api/SwingTrader.Api.csproj", "SwingTrader.Api/"]
COPY ["SwingTrader.Core/SwingTrader.Core.csproj", "SwingTrader.Core/"]
COPY ["SwingTrader.Data/SwingTrader.Data.csproj", "SwingTrader.Data/"]
COPY ["SwingTrader.Agents/SwingTrader.Agents.csproj", "SwingTrader.Agents/"]
COPY ["SwingTrader.Infrastructure/SwingTrader.Infrastructure.csproj", "SwingTrader.Infrastructure/"]

RUN dotnet restore "SwingTrader.Api/SwingTrader.Api.csproj"
COPY . .

WORKDIR "/src/SwingTrader.Api"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5001
ENTRYPOINT ["dotnet", "SwingTrader.Api.dll"]
```

### SwingTrader.Functions/Program.cs

```csharp
var host = new HostBuilder()
  .ConfigureFunctionsWorkerDefaults()
  .ConfigureAppConfiguration(config =>
  {
    config.AddEnvironmentVariables();
    var builtConfig = config.Build();
    var keyVaultUrl = builtConfig["KeyVaultUrl"];
    if (!string.IsNullOrEmpty(keyVaultUrl))
    {
      config.AddAzureKeyVault(
        new Uri(keyVaultUrl),
        new DefaultAzureCredential());
    }
  })
  .ConfigureServices((ctx, services) =>
  {
    services.AddDbContext<SwingTraderDbContext>(
      options => options.UseSqlServer(
        ctx.Configuration
          .GetConnectionString("DefaultConnection")));

    // Same DI registrations as API
    // (minus HTTP endpoints)
    services.AddApplicationInsightsTelemetryWorkerService();
  })
  .UseSerilog()
  .Build();

await host.RunAsync();
```

### Timer-Triggered Functions

Reference legacy BackgroundService workers.
Each becomes a timer-triggered Azure Function.

All cron expressions in UTC.
ET = UTC-5 (EST) / UTC-4 (EDT).
Use UTC-5 as base — 1hr early in summer,
function body checks market calendar.

```csharp
// ResearchFunction.cs
[Function("Research")]
public async Task Run(
  [TimerTrigger("0 0 11 * * 1-5")] // 6am ET
    TimerInfo timer,
  CancellationToken ct)

// WatchlistFunction.cs
[Function("Watchlist")]
public async Task Run(
  [TimerTrigger("0 0 1 * * 0")] // 8pm ET Sunday
    TimerInfo timer,
  CancellationToken ct)

// ReportFunction.cs
[Function("Report")]
public async Task Run(
  [TimerTrigger("0 30 11 * * 1-5")] // 6:30am ET
    TimerInfo timer,
  CancellationToken ct)

// ExecutionFunction.cs
[Function("Execution")]
public async Task Run(
  [TimerTrigger("0 20 14 * * 1-5")] // 9:20am ET
    TimerInfo timer,
  CancellationToken ct)

// MonitorFunction.cs
[Function("Monitor")]
public async Task Run(
  [TimerTrigger("0 */5 * * * *")] // Every 5 mins
    TimerInfo timer,
  CancellationToken ct)
// Body checks market hours before doing anything

// RiskFunction.cs
[Function("Risk")]
public async Task Run(
  [TimerTrigger("0 0 14 1 * *")] // 9am ET 1st of month
    TimerInfo timer,
  CancellationToken ct)

// RefinementFunction.cs
[Function("Refinement")]
public async Task Run(
  [TimerTrigger("0 0 13 15 * *")] // 8am ET 15th
    TimerInfo timer,
  CancellationToken ct)
```

### local.settings.json (Functions, gitignored)

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "KeyVaultUrl": "",
    "ConnectionStrings__DefaultConnection": ""
  }
}
```

Add to .gitignore:
```
local.settings.json
**/wwwroot/
```

### appsettings.json (Api)

```json
{
  "KeyVaultUrl": "",
  "ConnectionStrings": {
    "DefaultConnection": ""
  },
  "ApplicationInsights": {
    "ConnectionString": ""
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.EntityFrameworkCore": "Warning",
        "Microsoft.AspNetCore": "Warning"
      }
    }
  }
}
```

All sensitive values come from Key Vault.
Non-sensitive config sections (Trading212 base URLs,
rate limits, feature flags with safe defaults)
live in appsettings.json.

---

## Step 4: Bicep Infrastructure as Code

Create the infra/ folder structure:

```
infra/
├── main.bicep
├── modules/
│   ├── containerregistry.bicep
│   ├── appinsights.bicep
│   ├── keyvault.bicep
│   ├── keyvaultaccess.bicep
│   ├── sql.bicep
│   ├── containerapp.bicep
│   └── functionapp.bicep
└── parameters/
    ├── dev.bicepparam
    └── prod.bicepparam
```

### infra/main.bicep

```bicep
targetScope = 'resourceGroup'

@description('Environment name')
param environment string = 'prod'

@description('Azure region')
param location string = resourceGroup().location

@secure()
param sqlAdminPassword string

param adminUserId string = ''

var prefix = 'swingtrader'
var tags = {
  project: 'SwingTrader'
  environment: environment
}

module acr 'modules/containerregistry.bicep' = {
  name: 'acr'
  params: {
    name: '${prefix}cr${environment}'
    location: location
    tags: tags
  }
}

module appInsights 'modules/appinsights.bicep' = {
  name: 'appinsights'
  params: {
    name: '${prefix}-insights-${environment}'
    location: location
    tags: tags
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    name: '${prefix}-kv-${environment}'
    location: location
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    serverName: '${prefix}-sql-${environment}'
    databaseName: '${prefix}-db'
    location: location
    adminPassword: sqlAdminPassword
    sqlTier: 'Basic'
    sqlCapacity: 5
    tags: tags
  }
}

module containerApp 'modules/containerapp.bicep' = {
  name: 'containerapp'
  params: {
    environmentName: '${prefix}-env-${environment}'
    appName: '${prefix}-api-${environment}'
    location: location
    acrLoginServer: acr.outputs.loginServer
    appInsightsConnectionString:
      appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    tags: tags
  }
}

module functions 'modules/functionapp.bicep' = {
  name: 'functions'
  params: {
    name: '${prefix}-functions-${environment}'
    location: location
    appInsightsConnectionString:
      appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    tags: tags
  }
}

module kvAccessApi 'modules/keyvaultaccess.bicep' = {
  name: 'kvaccess-api'
  params: {
    keyVaultName: keyVault.outputs.name
    principalId: containerApp.outputs.principalId
    roleType: 'SecretsUser'
  }
}

module kvAccessFunctions 'modules/keyvaultaccess.bicep' = {
  name: 'kvaccess-functions'
  params: {
    keyVaultName: keyVault.outputs.name
    principalId: functions.outputs.principalId
    roleType: 'SecretsUser'
  }
}

output acrLoginServer string = acr.outputs.loginServer
output containerAppName string = containerApp.outputs.appName
output containerAppFqdn string = containerApp.outputs.fqdn
output functionAppName string = functions.outputs.name
output keyVaultName string = keyVault.outputs.name
output sqlConnectionString string = sql.outputs.connectionString
```

### infra/modules/containerregistry.bicep

```bicep
param name string
param location string
param tags object

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

output loginServer string = acr.properties.loginServer
output name string = acr.name
output resourceId string = acr.id
```

### infra/modules/appinsights.bicep

```bicep
param name string
param location string
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${name}-workspace'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    RetentionInDays: 30
  }
}

output connectionString string = appInsights.properties.ConnectionString
output instrumentationKey string = appInsights.properties.InstrumentationKey
```

### infra/modules/keyvault.bicep

```bicep
param name string
param location string
param tags object

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
    enabledForTemplateDeployment: true
  }
}

output uri string = keyVault.properties.vaultUri
output name string = keyVault.name
output resourceId string = keyVault.id
```

### infra/modules/keyvaultaccess.bicep

```bicep
param keyVaultName string
param principalId string

@allowed(['SecretsUser', 'CryptoOfficer'])
param roleType string = 'SecretsUser'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

var roles = {
  SecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  CryptoOfficer: '14b46e9e-c2b7-41b4-b07b-48a6ebf60603'
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, roles[roleType])
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      roles[roleType])
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
```

### infra/modules/sql.bicep

```bicep
param serverName string
param databaseName string
param location string
param tags object

@secure()
param adminPassword string

param sqlTier string = 'Basic'
param sqlCapacity int = 5

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    administratorLogin: 'sqladmin'
    administratorLoginPassword: adminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
  }
}

resource firewallAzure 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: sqlTier == 'Basic' ? 'Basic' : 'S0'
    tier: sqlTier
    capacity: sqlCapacity
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    requestedBackupStorageRedundancy: 'Local'
  }
}

output serverName string = sqlServer.name
output databaseName string = database.name
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
```

### infra/modules/containerapp.bicep

```bicep
param environmentName string
param appName string
param location string
param acrLoginServer string
param appInsightsConnectionString string
param keyVaultUri string
param tags object

resource containerAppEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
  }
}

resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: appName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 5001
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: acrLoginServer
          identity: 'system'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'swingtrader-api'
          image: '${acrLoginServer}/swingtrader-api:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:5001' }
            { name: 'KeyVaultUrl', value: keyVaultUri }
            {
              name: 'ApplicationInsights__ConnectionString'
              value: appInsightsConnectionString
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, appName, 'acrpull')
  scope: resourceGroup()
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output principalId string = containerApp.identity.principalId
output appName string = containerApp.name
output fqdn string = containerApp.properties.configuration.ingress.fqdn
```

### infra/modules/functionapp.bicep

```bicep
param name string
param location string
param appInsightsConnectionString string
param keyVaultUri string
param tags object

var storageAccountName = take(replace(name, '-', ''), 24)

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
}

resource hostingPlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${name}-plan'
  location: location
  tags: tags
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|9.0'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        { name: 'KeyVaultUrl', value: keyVaultUri }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

output principalId string = functionApp.identity.principalId
output name string = functionApp.name
```

### infra/parameters/prod.bicepparam

```bicep
using '../main.bicep'

param environment = 'prod'
param location = 'uksouth'
// sqlAdminPassword and adminUserId
// passed as GitHub secrets at deploy time
// never committed to repo
```

### infra/parameters/dev.bicepparam

```bicep
using '../main.bicep'

param environment = 'dev'
param location = 'uksouth'
```

---

## Step 5: GitHub Actions Workflows

### .github/workflows/ci.yml

```yaml
name: CI

on:
  pull_request:
    branches: [ main ]

jobs:
  test-dotnet:
    name: .NET Tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Restore
        run: dotnet restore SwingTrader.sln

      - name: Build
        run: dotnet build SwingTrader.sln
          --no-restore --configuration Release

      - name: Test
        run: dotnet test SwingTrader.sln
          --no-build --configuration Release
          --logger "trx;LogFileName=results.trx"
          --collect:"XPlat Code Coverage"

      - name: Publish test results
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: .NET Test Results
          path: '**/*.trx'
          reporter: dotnet-trx

  bicep-validate:
    name: Validate Bicep
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      contents: read
    steps:
      - uses: actions/checkout@v4

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Bicep lint
        run: az bicep lint --file infra/main.bicep

      - name: Bicep what-if (validate)
        run: |
          az deployment group what-if \
            --resource-group ${{ secrets.RESOURCE_GROUP }} \
            --template-file infra/main.bicep \
            --parameters infra/parameters/prod.bicepparam \
            --parameters sqlAdminPassword='placeholder' \
            --no-pretty-print
        continue-on-error: true
```

### .github/workflows/deploy-infra.yml

```yaml
name: Deploy Infrastructure

on:
  push:
    branches: [ main ]
    paths:
      - 'infra/**'
  workflow_dispatch:

permissions:
  id-token: write
  contents: read

jobs:
  deploy-infra:
    name: Deploy Bicep
    runs-on: ubuntu-latest
    environment: production

    steps:
      - uses: actions/checkout@v4

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Bicep lint
        run: az bicep lint --file infra/main.bicep

      - name: What-if preview
        run: |
          az deployment group what-if \
            --resource-group ${{ secrets.RESOURCE_GROUP }} \
            --template-file infra/main.bicep \
            --parameters infra/parameters/prod.bicepparam \
            --parameters sqlAdminPassword='${{ secrets.SQL_ADMIN_PASSWORD }}'

      - name: Deploy infrastructure
        id: deploy
        run: |
          output=$(az deployment group create \
            --resource-group ${{ secrets.RESOURCE_GROUP }} \
            --template-file infra/main.bicep \
            --parameters infra/parameters/prod.bicepparam \
            --parameters sqlAdminPassword='${{ secrets.SQL_ADMIN_PASSWORD }}' \
            --parameters adminUserId='${{ secrets.ADMIN_USER_ID }}' \
            --query properties.outputs \
            --output json)

          echo "KEY_VAULT_NAME=$(echo $output | jq -r '.keyVaultName.value')" >> $GITHUB_OUTPUT
          echo "SQL_CONN=$(echo $output | jq -r '.sqlConnectionString.value')" >> $GITHUB_OUTPUT
          echo "CONTAINER_APP_FQDN=$(echo $output | jq -r '.containerAppFqdn.value')" >> $GITHUB_OUTPUT

      - name: Populate Key Vault secrets
        run: |
          set_secret_if_missing() {
            local name=$1 value=$2
            if ! az keyvault secret show \
              --vault-name ${{ steps.deploy.outputs.KEY_VAULT_NAME }} \
              --name "$name" --query "id" -o tsv 2>/dev/null; then
              az keyvault secret set \
                --vault-name ${{ steps.deploy.outputs.KEY_VAULT_NAME }} \
                --name "$name" --value "$value"
              echo "Created: $name"
            else
              echo "Exists, skipping: $name"
            fi
          }

          set_secret_if_missing "Finnhub--ApiKey" "${{ secrets.FINNHUB_API_KEY }}"
          set_secret_if_missing "Tiingo--ApiKey" "${{ secrets.TIINGO_API_KEY }}"
          set_secret_if_missing "Trading212--ApiKey" "${{ secrets.T212_API_KEY }}"
          set_secret_if_missing "Trading212--ApiSecret" "${{ secrets.T212_API_SECRET }}"
          set_secret_if_missing "Claude--ApiKey" "${{ secrets.CLAUDE_API_KEY }}"
          set_secret_if_missing "Email--Username" "${{ secrets.EMAIL_USERNAME }}"
          set_secret_if_missing "Email--Password" "${{ secrets.EMAIL_PASSWORD }}"
          set_secret_if_missing "Email--ToAddresses--0" "${{ secrets.EMAIL_TO_0 }}"
          set_secret_if_missing "Email--ToAddresses--1" "${{ secrets.EMAIL_TO_1 }}"
          set_secret_if_missing "ConnectionStrings--DefaultConnection" "${{ steps.deploy.outputs.SQL_CONN }}"

      - name: Run EF migrations
        run: |
          dotnet tool install --global dotnet-ef
          dotnet ef database update \
            --project SwingTrader.Data \
            --startup-project SwingTrader.Api \
            --connection "${{ steps.deploy.outputs.SQL_CONN }}"

      - name: Output deployment info
        run: |
          echo "Container App URL: https://${{ steps.deploy.outputs.CONTAINER_APP_FQDN }}"
          echo "Add to UptimeRobot: https://${{ steps.deploy.outputs.CONTAINER_APP_FQDN }}/health"
```

### .github/workflows/deploy-api.yml

```yaml
name: Deploy API

on:
  push:
    branches: [ main ]
    paths:
      - 'SwingTrader.Api/**'
      - 'SwingTrader.Core/**'
      - 'SwingTrader.Data/**'
      - 'SwingTrader.Agents/**'
      - 'SwingTrader.Infrastructure/**'
      - 'SwingTrader.Angular/**'
  workflow_dispatch:

permissions:
  id-token: write
  contents: read

env:
  IMAGE_NAME: swingtrader-api

jobs:
  test:
    name: Tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet test SwingTrader.sln --configuration Release

  build-and-deploy:
    name: Build and Deploy
    runs-on: ubuntu-latest
    needs: test
    environment: production

    steps:
      - uses: actions/checkout@v4

      - name: Setup Node
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: SwingTrader.Angular/package-lock.json

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Install Angular dependencies
        run: npm ci
        working-directory: SwingTrader.Angular

      - name: Replace Angular environment tokens
        run: |
          sed -i "s|#{B2C_CLIENT_ID}#|${{ secrets.B2C_CLIENT_ID }}|g" SwingTrader.Angular/src/environments/environment.prod.ts
          sed -i "s|#{B2C_AUTHORITY}#|${{ secrets.B2C_AUTHORITY }}|g" SwingTrader.Angular/src/environments/environment.prod.ts
          sed -i "s|#{B2C_DOMAIN}#|${{ secrets.B2C_DOMAIN }}|g" SwingTrader.Angular/src/environments/environment.prod.ts
          sed -i "s|#{B2C_SCOPE}#|${{ secrets.B2C_SCOPE }}|g" SwingTrader.Angular/src/environments/environment.prod.ts
        continue-on-error: true
        # Tokens not yet set until Phase 10c
        # Step is safe to fail before that phase

      - name: Build Angular
        run: npm run build
        working-directory: SwingTrader.Angular
        continue-on-error: true
        # Angular not yet created until Phase 10b
        # Step is safe to fail before that phase

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Build and push to ACR
        run: |
          az acr build \
            --registry ${{ secrets.ACR_NAME }} \
            --image ${{ env.IMAGE_NAME }}:${{ github.sha }} \
            --image ${{ env.IMAGE_NAME }}:latest \
            --file SwingTrader.Api/Dockerfile \
            .

      - name: Deploy to Container App
        run: |
          az containerapp update \
            --name ${{ secrets.CONTAINER_APP_NAME }} \
            --resource-group ${{ secrets.RESOURCE_GROUP }} \
            --image ${{ secrets.ACR_NAME }}.azurecr.io/${{ env.IMAGE_NAME }}:${{ github.sha }}

      - name: Verify deployment
        run: |
          sleep 30
          APP_URL=$(az containerapp show \
            --name ${{ secrets.CONTAINER_APP_NAME }} \
            --resource-group ${{ secrets.RESOURCE_GROUP }} \
            --query properties.configuration.ingress.fqdn \
            -o tsv)
          response=$(curl -s -o /dev/null -w "%{http_code}" \
            "https://${APP_URL}/health/ready")
          if [ "$response" != "200" ]; then
            echo "Health check failed: $response"
            exit 1
          fi
          echo "Deployed successfully: https://$APP_URL"
```

### .github/workflows/deploy-functions.yml

```yaml
name: Deploy Functions

on:
  push:
    branches: [ main ]
    paths:
      - 'SwingTrader.Functions/**'
      - 'SwingTrader.Core/**'
      - 'SwingTrader.Data/**'
      - 'SwingTrader.Agents/**'
      - 'SwingTrader.Infrastructure/**'
  workflow_dispatch:

permissions:
  id-token: write
  contents: read

jobs:
  test:
    name: Tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet test SwingTrader.sln --configuration Release

  build-and-deploy:
    name: Build and Deploy
    runs-on: ubuntu-latest
    needs: test
    environment: production

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Build Functions
        run: |
          dotnet publish SwingTrader.Functions \
            --configuration Release \
            --output ./functions-output \
            --runtime linux-x64 \
            --self-contained false

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Deploy to Function App
        uses: azure/functions-action@v1
        with:
          app-name: ${{ secrets.FUNCTION_APP_NAME }}
          package: ./functions-output
          scm-do-build-during-deployment: false

      - name: Verify
        run: |
          state=$(az functionapp show \
            --name ${{ secrets.FUNCTION_APP_NAME }} \
            --resource-group ${{ secrets.RESOURCE_GROUP }} \
            --query state -o tsv)
          echo "Function App state: $state"
```

---

## Step 6: OIDC Bootstrap (One-Time Manual)

Run these commands once before any deployment.
Everything after is automated.

```bash
# Create resource group
az group create \
  --name swingtrader-rg \
  --location uksouth

# Create app registration for GitHub Actions
az ad app create --display-name swingtrader-github-actions

APP_ID=$(az ad app list \
  --display-name swingtrader-github-actions \
  --query "[0].appId" -o tsv)

# Create service principal
az ad sp create --id $APP_ID

SP_OBJECT_ID=$(az ad sp show \
  --id $APP_ID --query id -o tsv)

# Grant Contributor on resource group
az role assignment create \
  --assignee $SP_OBJECT_ID \
  --role Contributor \
  --scope /subscriptions/$(az account show --query id -o tsv)/resourceGroups/swingtrader-rg

# Federated credential for main branch pushes
az ad app federated-credential create \
  --id $APP_ID \
  --parameters '{
    "name": "github-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:markieshorty/SwingTrader:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenCredential"]
  }'

# Federated credential for pull requests
az ad app federated-credential create \
  --id $APP_ID \
  --parameters '{
    "name": "github-prs",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:markieshorty/SwingTrader:pull_request",
    "audiences": ["api://AzureADTokenCredential"]
  }'

# Print the values needed for GitHub secrets
echo "AZURE_CLIENT_ID: $APP_ID"
echo "AZURE_TENANT_ID: $(az account show --query tenantId -o tsv)"
echo "AZURE_SUBSCRIPTION_ID: $(az account show --query id -o tsv)"
```

Add these three values as GitHub secrets.
Then add all remaining secrets from the
list in 00_README_ClaudeCodeInstructions.md.

---

## Step 7: UptimeRobot (One-Time Manual)

After first deployment:

1. Sign up at uptimerobot.com (free)
2. Add monitor:
   Type: HTTP(S)
   URL: https://{container-app-fqdn}/health
   Interval: 10 minutes
   Alert email: yours

The FQDN is output in the deploy-infra.yml
workflow log under "Output deployment info".

This keeps the Container App warm during
the day. Cold starts only happen after
10+ minutes of inactivity.

---

## Deliverables

1. dotnet build SwingTrader.sln
   Zero errors, zero warnings

2. dotnet test SwingTrader.sln
   All tests pass (initial count may be low
   — will grow as core logic is ported)

3. az bicep lint --file infra/main.bicep
   Zero errors

4. GitHub Actions CI workflow runs on a PR:
   .NET tests pass
   Bicep validates

5. Push infra/ change to main:
   deploy-infra.yml completes successfully
   All Azure resources visible in portal
   under swingtrader-rg
   Key Vault secrets populated
   EF migrations applied

6. Push SwingTrader.Api change to main:
   deploy-api.yml runs
   Docker image built and pushed to ACR
   Container App updated
   Health check returns 200

7. Push SwingTrader.Functions change to main:
   deploy-functions.yml runs
   All 7 functions visible in portal

8. https://{container-app-fqdn}/health
   Returns healthy JSON

9. UptimeRobot showing UP status

10. No manual Azure CLI or Portal clicks
    were needed after the OIDC bootstrap
