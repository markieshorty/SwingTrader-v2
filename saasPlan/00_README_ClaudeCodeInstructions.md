# SwingTrader — Claude Code Instructions

new git repo: https://github.com/markieshorty/SwingTrader-v2.git
local path to clone into. C:\Code\swingTrader-v2

"WatchlistAgent_DynamicUniverse.md
    Read before implementing any watchlist
    agent logic. Replaces the hardcoded
    230-symbol universe from v1."

## What This Project Is

SwingTrader is an autonomous swing trading
system for US equities. It researches stocks
every morning, sends a daily email brief,
places trades via Trading 212 on approval,
monitors positions throughout the day,
and learns from its own trade history
via a monthly refinement loop.

This is a greenfield build in a clean repo.
A legacy implementation exists at
markieshorty/SwingTrader (private) which
contains working implementations of all
core agents, services, and infrastructure.
Reference it for implementation patterns,
copy relevant code where appropriate, but
do not be constrained by its structure.

## Technology Stack

Backend:       .NET 9, ASP.NET Core
ORM:           EF Core 9, Azure SQL
Agents:        BackgroundService (local dev)
               Azure Functions (production)
Queue:         Azure Service Bus
Auth:          Azure AD B2C, MSAL
Frontend:      Angular 17+, Angular Material,
               AG Grid, Chart.js
IaC:           Bicep
CI/CD:         GitHub Actions (mono-repo)
Secrets:       Azure Key Vault
Observability: Application Insights, Serilog

## Mono-Repo Structure

```
SwingTrader/
├── .github/
│   └── workflows/
│       ├── ci.yml
│       ├── deploy-infra.yml
│       ├── deploy-api.yml
│       └── deploy-functions.yml
├── infra/
│   ├── main.bicep
│   ├── modules/
│   └── parameters/
├── SwingTrader.Core/
├── SwingTrader.Data/
├── SwingTrader.Agents/
├── SwingTrader.Infrastructure/
├── SwingTrader.Api/
│   └── Dockerfile
├── SwingTrader.Functions/
├── SwingTrader.Tests/
├── SwingTrader.Angular/
└── SwingTrader.sln
```

## Phase Documents

Hand these to Claude Code one at a time.
Complete all deliverables before moving
to the next phase.

```
01_Phase10a_AzureInfra_CICD_IaC.md
  Azure infrastructure via Bicep
  GitHub Actions CI/CD pipeline
  Solution structure
  Database setup (Azure SQL)
  Core API and Functions projects

02_Phase10b_AngularFrontend.md
  Angular 17+ workspace
  Material + AG Grid + Charts
  All dashboard pages
  Auth shell (inactive until Phase 10c)

03_Phase10c_MultiTenancyAuth.md
  Azure AD B2C with Google Sign-On
  Account entity as tenant/billing unit,
    multiple AppUsers per account via
    invite links (own login each)
  AccountId on all database tables
  Row-level data isolation per account
  JWT validation on all API endpoints

04_Phase10d_KeyEncryptionScheduling.md
  Per-user AES-256 + Key Vault encryption
  Service Bus queue-based scheduling
  Per-user HTTP clients
  Settings page fully implemented

05_Phase10e_Onboarding_10f_GlobalRefinement.md
  Phase 10e: 5-step onboarding wizard
  Phase 10f: Anonymised global data pooling

06_Phase10g_RiskWatchlistAdmin.md
  Per-user risk profile customisation
  Multiple named watchlists
  Admin area for user management
```

## Rules For Claude Code

1. Read the entire phase spec before
   writing any code.

2. Run dotnet test after every phase.
   All tests must pass before proceeding.

3. Each phase lists explicit deliverables.
   Verify every deliverable before marking
   the phase complete.

4. Do not add multi-tenancy (AccountId,
   UserId, per-account/per-user anything)
   before Phase 10c. Phases must stay
   clean and isolated.

5. When the spec says to reference the
   legacy repo, look there for working
   implementation patterns. Do not copy
   blindly — adapt to the new structure.

6. Never store secrets in code, config
   files, or environment variables in
   the repo. All secrets go to Key Vault.
   Local development uses dotnet user-secrets.

7. Bicep is the only way to create Azure
   resources. No manual Azure Portal
   clicks or CLI commands except the
   one-time OIDC bootstrap.

## GitHub Secrets Required

Set these in GitHub before any deployment:

```
# Azure OIDC (one-time setup)
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID

# Azure Resources
RESOURCE_GROUP
ACR_NAME
CONTAINER_APP_NAME
FUNCTION_APP_NAME

# Database
SQL_ADMIN_PASSWORD

# Azure AD B2C (Phase 10c)
B2C_CLIENT_ID
B2C_TENANT_ID
B2C_AUTHORITY
B2C_DOMAIN
B2C_SCOPE

# API Keys (for Key Vault population)
FINNHUB_API_KEY
TIINGO_API_KEY
T212_API_KEY
T212_API_SECRET
CLAUDE_API_KEY
EMAIL_USERNAME
EMAIL_PASSWORD
EMAIL_TO_0
EMAIL_TO_1

# Admin (Phase 10c+)
ADMIN_USER_ID
```

## Legacy Repo Reference Guide

The legacy repo contains working
implementations. Key files to reference:

```
Core domain:
  SwingTrader.Core/Entities/
    Trade.cs, StockSignal.cs,
    WatchlistItem.cs, StrategyWeights.cs
    (all entities with full field definitions)

Research pipeline:
  SwingTrader.Agents/Research/
    ResearchPipeline.cs
    ConvictionScorer.cs
    (7-step pipeline, all component scoring)

All agents:
  WatchlistAgent, ReportAgent,
  ExecutionAgent, MonitorAgent,
  RefinementAgent, RiskAgent
  (full working implementations)

Infrastructure clients:
  SwingTrader.Infrastructure/
    FinnhubClient, TiingoClient,
    ClaudeClient, Trading212Client
    (Refit clients with all DTOs)

Conviction components (Phases 9b-9e):
  EarningsCalendarService
  RelativeStrengthService
  PriceLevelMemoryService
  FundamentalDataService
  FundamentalScoringService

Regime detection:
  MarketRegimeService
  (SPY vs MA, VIX thresholds)

Refinement engine:
  ComponentCorrelationService
  RefinementService
  ApplyRefinementService
  (full Phase 6b + 6c implementation)

Readiness assessment:
  ReadinessAssessmentService
  WilsonInterval calculation
  (full Phase 7 readiness dashboard)

Database:
  SwingTrader.Data/
    SwingTraderDbContext.cs
    All migrations
    All repository implementations
```

## Deployment Flow (After Phase 10a)

```
Make code changes
↓
Open PR → ci.yml runs tests
↓
Merge to main
↓
If infra/ changed:
  deploy-infra.yml → Bicep → Azure resources
  + Key Vault population
  + EF migrations

If Api/Core/Data/Agents/
   Infrastructure/Angular changed:
  deploy-api.yml → Angular build
  → Docker build → ACR push
  → Container App update
  → Health check verify

If Functions/Core/Data/
   Agents/Infrastructure changed:
  deploy-functions.yml → dotnet publish
  → Azure Functions deploy
```

## Local Development Setup

```bash
# SQL Server via Docker
docker run -e "ACCEPT_EULA=Y" \
  -e "SA_PASSWORD=YourStrong@Passw0rd" \
  -p 1433:1433 --name swingtrader-sql \
  -d mcr.microsoft.com/mssql/server:2022-latest

# Set user secrets (SwingTrader.Api)
dotnet user-secrets set \
  "ConnectionStrings:DefaultConnection" \
  "Server=localhost;Database=SwingTrader;..."

# Apply migrations
dotnet ef database update \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api

# Run API
cd SwingTrader.Api && dotnet run

# Run Angular dev server (separate terminal)
cd SwingTrader.Angular && ng serve

# Run Functions locally (separate terminal)
cd SwingTrader.Functions && func start
```
