# SwingTrader

Autonomous swing trading system for US equities. See `saasPlan/` for the
full phased build spec.

## Local Development Setup

### SQL Server (Docker)

```bash
docker run -e "ACCEPT_EULA=Y" \
  -e "SA_PASSWORD=YourStrong@Passw0rd" \
  -p 1433:1433 --name swingtrader-sql \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

### API (.NET)

```bash
# Set the local connection string (once)
dotnet user-secrets set \
  "ConnectionStrings:DefaultConnection" \
  "Server=localhost;Database=SwingTrader;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True" \
  --project SwingTrader.Api

# Apply migrations
dotnet ef database update \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api

# Run the API (listens on http://localhost:5001)
cd SwingTrader.Api && dotnet run
```

### Angular

The Angular workspace lives in `SwingTrader.Angular/`. Its production
build output goes directly into `SwingTrader.Api/wwwroot` (see
`angular.json`'s `outputPath`), so the same Container App serves both the
SPA and the `/api/*` endpoints in production. `wwwroot` is gitignored —
it's a build artifact, not source.

```bash
cd SwingTrader.Angular
npm install

# Dev server against the local API (see src/environments/environment.ts)
npm start                # http://localhost:4200
# Calls the API directly at http://localhost:5001 (cross-origin, allowed
# by the "Angular" CORS policy already configured in SwingTrader.Api)

# Production-style build (writes to ../SwingTrader.Api/wwwroot)
npm run build

# Unit tests (headless Chrome)
npm test
```

During local development, run the API (`:5001`) and the Angular dev
server (`:4200`) side by side — `environment.ts` points the dev build at
`http://localhost:5001`. `environment.prod.ts` uses an empty `apiUrl`
(same-origin), since the built SPA is served by the API itself in
Container Apps.

### Regenerating API Types

`SwingTrader.Angular/src/app/core/models/dtos.ts` is currently
hand-written, since the real `/api/*` endpoints (portfolio, positions,
signals, trades, refinement, readiness) don't exist yet — they land when
`SwingTrader.Agents`/`SwingTrader.Infrastructure` business logic is
ported in a later phase. Once those endpoints are real:

```bash
# With the API running locally on :5001
cd SwingTrader.Angular
npm run generate-api
```

This regenerates TypeScript interfaces from `/swagger/v1/swagger.json`
into `src/app/core/models/generated`. Delete `dtos.ts` and switch
`api.service.ts`'s imports over once that's done — never hand-write
interfaces that duplicate the real C# DTOs going forward.

### Functions

```bash
cd SwingTrader.Functions
func start
```

## Deployment

See `saasPlan/00_README_ClaudeCodeInstructions.md` for the full CI/CD
pipeline, Azure infrastructure, and phase-by-phase build plan.
