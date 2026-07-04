# SwingTrader — Phase 10c
# Multi-Tenancy and Google Authentication

## Context
Phase 10a complete: Azure infrastructure,
CI/CD pipeline, API and Functions deployed.
Phase 10b complete: Angular frontend live.

Phase 10c adds:
  Google Sign-On via Azure AD B2C
  AccountId on every database table
  Row-level data isolation per account
  Multiple users per account, each with
    their own Google/Facebook login
    (invite flow — no shared credentials)
  JWT validation on all API endpoints
  MSAL Angular activated

After this phase: each ACCOUNT sees only
its own data. An account is the tenant/
billing unit — an account owner can invite
other people to share their account (e.g.
a friend or partner), and each invited
person signs in with their own identity
provider login. Accounts do not share
data with each other. Users within the
same account share all of that account's
data (trades, watchlists, settings).

---

## Step 1: Azure AD B2C (Manual Setup)

B2C cannot be created via Bicep — it
requires a separate tenant. This is the
only manual step in Phase 10c.

### Create B2C Tenant

1. Azure Portal → Create resource
2. Search: "Azure Active Directory B2C"
3. Create new B2C tenant:
   Organization name: SwingTrader
   Initial domain: swingtrader
   Country: United Kingdom
4. Note the tenant domain:
   swingtrader.onmicrosoft.com

### Register Application in B2C

1. Switch to the B2C tenant directory
2. App registrations → New registration
3. Name: SwingTrader App
4. Supported account types:
   "Accounts in any identity provider"
5. Redirect URI type: SPA
   Value: https://{container-app-fqdn}
6. Add second redirect: http://localhost:4200
7. Note the Application (client) ID

### Configure Google Identity Provider

First set up in Google Cloud Console:
1. console.cloud.google.com
2. APIs & Services → Credentials
3. Create OAuth 2.0 Client ID
4. Application type: Web application
5. Authorised redirect URIs:
   https://swingtrader.b2clogin.com/
     swingtrader.onmicrosoft.com/
     oauth2/authresp
6. Note Client ID and Client Secret

Then in B2C:
1. Identity providers → Google
2. Client ID: (from Google Cloud Console)
3. Client Secret: (from Google Cloud Console)

### Create User Flow

1. B2C → User flows → New user flow
2. Sign up and sign in (Recommended)
3. Name: B2C_1_signupsignin
4. Identity providers: Google
5. User attributes to collect:
   Display name, Email address
6. Application claims to return:
   Display name, Email addresses,
   Identity provider,
   User's Object ID (sub)
7. Create

### Add GitHub Secrets

After B2C setup, add to GitHub secrets:

```
B2C_CLIENT_ID   = Application (client) ID from step above
B2C_TENANT_ID   = B2C tenant ID
B2C_AUTHORITY   = https://swingtrader.b2clogin.com/swingtrader.onmicrosoft.com/B2C_1_signupsignin
B2C_DOMAIN      = swingtrader.b2clogin.com
B2C_SCOPE       = https://swingtrader.onmicrosoft.com/api/user_impersonation
ADMIN_USER_ID   = your B2C object ID
                  (found after first sign-in:
                   B2C → Users → your account → Object ID)
```

### Add B2C Secrets to Key Vault

Add to deploy-infra.yml Key Vault
population step (or run manually once):

```bash
az keyvault secret set \
  --vault-name swingtrader-kv-prod \
  --name AzureAdB2C--ClientId \
  --value "{B2C_CLIENT_ID}"

az keyvault secret set \
  --vault-name swingtrader-kv-prod \
  --name Admin--UserId \
  --value "{ADMIN_USER_ID}"
```

---

## Step 2: Bicep Updates

### Update infra/modules/containerapp.bicep

Add B2C configuration to environment
variables in containerapp.bicep:

```bicep
// Add these params at top:
param b2cAuthority string = ''
param b2cClientId string = ''

// Add to env array in template.containers[0].env:
{
  name: 'AzureAdB2C__Authority'
  value: b2cAuthority
}
{
  name: 'AzureAdB2C__ClientId'
  value: b2cClientId
}
```

### Update infra/main.bicep

Add params and pass to containerApp module:

```bicep
param b2cAuthority string = ''
param b2cClientId string = ''

// In containerApp module call, add:
b2cAuthority: b2cAuthority
b2cClientId: b2cClientId
```

### Update infra/parameters/prod.bicepparam

```bicep
// Add (values from GitHub secrets at deploy time):
// b2cAuthority and b2cClientId are passed
// as --parameters in the workflow, not here
// to avoid committing them to the repo
```

### Update deploy-infra.yml

Add B2C params to the deployment command:

```yaml
- name: Deploy infrastructure
  run: |
    output=$(az deployment group create \
      --resource-group ${{ secrets.RESOURCE_GROUP }} \
      --template-file infra/main.bicep \
      --parameters infra/parameters/prod.bicepparam \
      --parameters sqlAdminPassword='${{ secrets.SQL_ADMIN_PASSWORD }}' \
      --parameters adminUserId='${{ secrets.ADMIN_USER_ID }}' \
      --parameters b2cAuthority='${{ secrets.B2C_AUTHORITY }}' \
      --parameters b2cClientId='${{ secrets.B2C_CLIENT_ID }}' \
      --query properties.outputs \
      --output json)
```

---

## Step 3: Database Changes

The tenant/billing unit is the **Account**,
not the individual login. An Account can
have multiple AppUsers attached to it (e.g.
an owner invites a partner or friend), and
each AppUser signs in with their own Google/
Facebook identity — nobody shares a login.
All trading data (Trades, WatchlistItems,
StrategyWeights, etc.) is scoped by
AccountId. Users within the same account
see the same data; different accounts never
see each other's data.

### AppUser Entity (SwingTrader.Core)

Represents one login identity (one B2C
object ID). An AppUser can belong to
exactly one Account at a time.

```csharp
public class AppUser : BaseEntity
{
  public string UserId { get; set; }
    // B2C object ID — stable unique identifier
    // for this login (Google/Facebook/etc.)
  public string Email { get; set; }
  public string DisplayName { get; set; }
  public int? AccountId { get; set; }
    // null until they create or join an account
  public AccountRole Role { get; set; }
    // Owner or Member, within their AccountId
  public DateTime FirstLoginAt { get; set; }
  public DateTime LastLoginAt { get; set; }
  public bool IsActive { get; set; } = true;
  public bool IsSuspended { get; set; } = false;
  public DateTime? SuspendedAt { get; set; }
  public bool IsOnboarded { get; set; } = false;
  public int OnboardingStep { get; set; } = 0;
}
```

Note: AppUser does NOT inherit BaseEntity's
AccountId scoping in the row-isolation sense
(AppUser.AccountId above is a plain nullable
foreign key, not the inherited scoping field)
— AppUser records must remain queryable by
UserId (from the JWT) before an AccountId is
even known, e.g. during first login and
invite acceptance.

### Account Entity (SwingTrader.Core)

The tenant/billing unit. All scoped data
hangs off AccountId.

```csharp
public class Account : BaseEntity
{
  public string Name { get; set; }
    = "My Account";
  public DateTime CreatedAt { get; set; }
  public string? T212AccountId { get; set; }
  public bool GlobalRefinementOptIn
    { get; set; } = false;
}
```

(Settings that used to live on AppUser in a
single-user-per-tenant model — T212AccountId,
GlobalRefinementOptIn — move to Account,
since they're shared by everyone in the
account.)

### AccountInvite Entity (SwingTrader.Core)

```csharp
public class AccountInvite
{
  public int Id { get; set; }
  public int AccountId { get; set; }
  public string InvitedByUserId { get; set; }
  public string InvitedEmail { get; set; }
  public string Token { get; set; }
    // opaque random token, sent in invite link
  public DateTime CreatedAt { get; set; }
  public DateTime ExpiresAt { get; set; }
    // e.g. CreatedAt + 7 days
  public DateTime? AcceptedAt { get; set; }
  public string? AcceptedByUserId { get; set; }
}
```

### AccountRole Enum (SwingTrader.Core)

```csharp
public enum AccountRole
{
  Owner,
    // created the account; can invite/remove
    // members, manage billing/settings
  Member
    // invited; full access to shared trading
    // data, cannot invite/remove others
}
```

### Add AccountId to All Scoped Entities

Add to BaseEntity:

```csharp
public class BaseEntity
{
  public int Id { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
  public int AccountId { get; set; }
}
```

Every trading-data entity (Trade,
WatchlistItem, StockSignal, StrategyWeights,
DailyReport, PortfolioSnapshot, etc.)
inherits AccountId automatically. Entities
that existed before this phase get AccountId
via migration pointing at a single 'system'
Account created during the migration
(representing your original single-tenant
data) — see Step 6.

AppUser, Account, and AccountInvite do NOT
inherit BaseEntity's row-isolation semantics
in the usual way (AppUser/AccountInvite are
looked up by UserId/Token before an account
context exists; Account is looked up by its
own Id). Give them their own simple base
(Id, CreatedAt, UpdatedAt) instead if a
shared BaseEntity would force an AccountId
they don't have yet.

### IAccountContext

```csharp
// SwingTrader.Core/Interfaces/IAccountContext.cs
public interface IAccountContext
{
  string UserId { get; }
  string Email { get; }
  int AccountId { get; }
  AccountRole Role { get; }
  bool IsAuthenticated { get; }
}

// SwingTrader.Api/Services/AccountContext.cs
// Resolves the caller's AppUser (by "sub"
// claim) to their AccountId/Role. Populated
// once per request, e.g. via a scoped service
// that loads AppUser after authentication
// runs (or via claims set by
// UserRegistrationMiddleware — see below).
public class AccountContext : IAccountContext
{
  private readonly IHttpContextAccessor _accessor;

  public AccountContext(
    IHttpContextAccessor accessor)
  {
    _accessor = accessor;
  }

  public string UserId =>
    _accessor.HttpContext?.User
      .FindFirst("sub")?.Value
    ?? throw new UnauthorizedAccessException(
      "No authenticated user");

  public string Email =>
    _accessor.HttpContext?.User
      .FindFirst("emails")?.Value
      ?? string.Empty;

  public int AccountId =>
    int.Parse(_accessor.HttpContext?.Items
      ["AccountId"]?.ToString()
    ?? throw new UnauthorizedAccessException(
      "User has no account"));

  public AccountRole Role =>
    Enum.Parse<AccountRole>(
      _accessor.HttpContext?.Items
        ["AccountRole"]?.ToString()
      ?? nameof(AccountRole.Member));

  public bool IsAuthenticated =>
    _accessor.HttpContext?.User
      .Identity?.IsAuthenticated ?? false;
}

// SwingTrader.Functions/Services/
//   FunctionAccountContext.cs
// Functions run per-account on a schedule,
// so AccountId is set explicitly by the
// caller (e.g. the timer trigger loops over
// all active Accounts), not from a JWT.
public class FunctionAccountContext
  : IAccountContext
{
  public string UserId { get; set; }
    = string.Empty;
  public string Email { get; set; }
    = string.Empty;
  public int AccountId { get; set; }
  public AccountRole Role { get; set; }
    = AccountRole.Owner;
  public bool IsAuthenticated =>
    !string.IsNullOrEmpty(UserId);
}
```

`HttpContext.Items["AccountId"]` /
`["AccountRole"]` are set by
UserRegistrationMiddleware below, once per
request, right after it resolves (or
creates) the caller's AppUser — this avoids
every request re-querying AppUser twice.

### Repository Pattern Change

All repositories inject IAccountContext and
filter every query by AccountId:

```csharp
// Example: TradeRepository
public class TradeRepository : ITradeRepository
{
  private readonly SwingTraderDbContext _context;
  private readonly IAccountContext _accountContext;

  public async Task<List<Trade>>
    GetOpenTradesAsync(CancellationToken ct)
  {
    return await _context.Trades
      .Where(t =>
        t.AccountId == _accountContext.AccountId &&
        t.Status == TradeStatus.Open)
      .ToListAsync(ct);
  }

  public async Task<Trade> AddAsync(
    Trade trade, CancellationToken ct)
  {
    trade.AccountId = _accountContext.AccountId;
    _context.Trades.Add(trade);
    await _context.SaveChangesAsync(ct);
    return trade;
  }
}
```

Apply this pattern to every repository method.
No query should ever return data for a
different AccountId than the current
account. Multiple AppUsers with the same
AccountId see identical data — that is the
intended behaviour, not a leak.

### User Registration + Account Resolution Middleware

Handles three cases per request: brand-new
login (no AppUser yet, no invite token —
create a new Account, make them Owner),
invite acceptance (no AppUser yet, valid
invite token present — join the inviter's
Account as Member), and returning user
(AppUser exists — just refresh AccountId/
Role into HttpContext.Items).

```csharp
// SwingTrader.Api/Middleware/
//   UserRegistrationMiddleware.cs
public class UserRegistrationMiddleware
{
  private readonly RequestDelegate _next;

  public async Task InvokeAsync(
    HttpContext context,
    IUserRepository users,
    IAccountRepository accounts,
    IAccountInviteRepository invites,
    IWatchlistRepository watchlists,
    IStrategyWeightsRepository weights)
  {
    if (context.User.Identity?.IsAuthenticated
        == true)
    {
      var userId = context.User
        .FindFirst("sub")?.Value!;
      var email = context.User
        .FindFirst("emails")?.Value
        ?? string.Empty;
      var user = await users.FindAsync(userId);

      if (user is null)
      {
        // check for a pending invite token,
        // e.g. passed as ?invite= on first
        // redirect and stashed client-side,
        // then forwarded as a header
        var inviteToken = context.Request
          .Headers["X-Invite-Token"]
          .FirstOrDefault();

        AccountInvite? invite = inviteToken is null
          ? null
          : await invites
              .FindValidByTokenAsync(inviteToken);

        int accountId;
        AccountRole role;

        if (invite is not null)
        {
          accountId = invite.AccountId;
          role = AccountRole.Member;
          await invites.MarkAcceptedAsync(
            invite.Id, userId);
        }
        else
        {
          var account = await accounts
            .CreateAsync(new Account());
          accountId = account.Id;
          role = AccountRole.Owner;

          // Seed defaults for a brand-new account
          await watchlists
            .SeedDefaultAsync(accountId);
          await weights
            .SeedDefaultAsync(accountId);
        }

        user = new AppUser
        {
          UserId = userId,
          Email = email,
          DisplayName = context.User
            .FindFirst("name")?.Value ?? email,
          AccountId = accountId,
          Role = role,
          FirstLoginAt = DateTime.UtcNow,
          LastLoginAt = DateTime.UtcNow
        };

        await users.CreateAsync(user);
      }
      else
      {
        await users.UpdateLastLoginAsync(userId);
      }

      context.Items["AccountId"] =
        user.AccountId!.Value.ToString();
      context.Items["AccountRole"] =
        user.Role.ToString();
    }

    await _next(context);
  }
}
```

Register in Program.cs:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<UserRegistrationMiddleware>();
```

### Invite Endpoints

```csharp
// Owner-only: create an invite
app.MapPost("/api/account/invites",
  async (InviteRequest req,
    IAccountInviteRepository invites,
    IAccountContext ctx) =>
  {
    if (ctx.Role != AccountRole.Owner)
      return Results.Forbid();

    var invite = new AccountInvite
    {
      AccountId = ctx.AccountId,
      InvitedByUserId = ctx.UserId,
      InvitedEmail = req.Email,
      Token = Guid.NewGuid().ToString("N"),
      CreatedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.AddDays(7)
    };
    await invites.CreateAsync(invite);

    // Return a link the owner shares directly
    // (e.g. via their own email/message) —
    // no email is sent automatically
    return Results.Ok(new
    {
      inviteUrl =
        $"{req.AppBaseUrl}/join?invite={invite.Token}"
    });
  }).RequireAuthorization();

// Owner-only: list/remove members
app.MapGet("/api/account/members",
  ...).RequireAuthorization();
app.MapDelete("/api/account/members/{userId}",
  ...).RequireAuthorization();
  // Owner-only, cannot remove self if sole Owner
```

The `/join?invite={token}` Angular route
reads the token from the URL, stores it
(e.g. sessionStorage), triggers Google
sign-in, and the API client attaches it as
the `X-Invite-Token` header on the very
first authenticated request so the
middleware above can pick it up.

### Migration: AddMultiTenancy

```bash
dotnet ef migrations add AddMultiTenancy \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

Migration should:
  Create Accounts table, with one row
    (Id = the 'system' account) representing
    your original single-tenant data
  Create AppUsers table
  Create AccountInvites table
  Add AccountId INT NOT NULL DEFAULT
    {system-account-id} to all previously
    single-tenant entity tables
  Add indexes on AccountId for all tables:
    CREATE INDEX IX_{Table}_AccountId
    ON {Table} (AccountId)
  Add FK constraints from AccountId columns
    to Accounts(Id)

Verify the generated migration SQL before
applying. The 'system' Account default
ensures existing data is preserved and
automatically owned by whichever AppUser
you create for yourself in Step 6.

---

## Step 4: API Authentication

### NuGet Packages

```bash
dotnet add SwingTrader.Api package \
  Microsoft.Identity.Web
dotnet add SwingTrader.Api package \
  Microsoft.AspNetCore.Authentication.JwtBearer
```

### Program.cs Authentication Setup

```csharp
// Add after builder.Services declarations:

builder.Services.AddAuthentication(
  JwtBearerDefaults.AuthenticationScheme)
  .AddJwtBearer(options =>
  {
    options.Authority = builder.Configuration
      ["AzureAdB2C:Authority"];
    options.Audience = builder.Configuration
      ["AzureAdB2C:ClientId"];
    options.TokenValidationParameters =
      new TokenValidationParameters
      {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        NameClaimType = "name",
      };
  });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<IUserContext,
  UserContext>();

// All repositories now also need
// IUserContext injected

// After app.Build():
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<UserRegistrationMiddleware>();
```

### Protect All Endpoints

Add RequireAuthorization() to all
API routes except health and approve:

```csharp
// Protect all /api/* routes
var api = app.MapGroup("/api")
  .RequireAuthorization();

// Health stays public
app.MapHealthChecks("/health", ...);
app.MapHealthChecks("/health/ready", ...);
app.MapHealthChecks("/health/live", ...);

// Approve endpoint stays public
// (token in query string IS the auth)
app.MapGet("/approve", ApproveHandler);
```

### appsettings.json Addition

```json
"AzureAdB2C": {
  "Instance": "https://swingtrader.b2clogin.com",
  "ClientId": "",
  "Domain": "swingtrader.onmicrosoft.com",
  "SignUpSignInPolicyId": "B2C_1_signupsignin",
  "Authority": ""
}
```

Values come from Key Vault — empty here.

---

## Step 5: Angular Authentication

### Activate MSAL

Phase 10b installed MSAL as an inactive
shell. Phase 10c activates it.

Update src/app/app.config.ts:

```typescript
import {
  MsalModule, MsalService, MsalGuard,
  MsalInterceptor, MsalBroadcastService
} from '@azure/msal-angular';
import {
  PublicClientApplication, InteractionType,
  BrowserCacheLocation
} from '@azure/msal-browser';

const msalConfig = {
  auth: {
    clientId: environment.b2cClientId,
    authority: environment.b2cAuthority,
    knownAuthorities: [environment.b2cDomain],
    redirectUri: window.location.origin
  },
  cache: {
    cacheLocation: BrowserCacheLocation.SessionStorage
  }
};

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes,
      withComponentInputBinding()),
    provideHttpClient(
      withInterceptors([
        authInterceptor,
        errorInterceptor
      ])
    ),
    importProvidersFrom(
      MsalModule.forRoot(
        new PublicClientApplication(msalConfig),
        {
          interactionType: InteractionType.Redirect,
          authRequest: {
            scopes: ['openid', 'profile',
              environment.b2cScope]
          }
        },
        {
          interactionType: InteractionType.Redirect,
          protectedResourceMap: new Map([
            [
              `${environment.apiUrl}/api/*`,
              [environment.b2cScope]
            ]
          ])
        }
      )
    )
  ]
};
```

### Auth Guard (Activate)

```typescript
// src/app/core/guards/auth.guard.ts
export const authGuard: CanActivateFn =
  (route, state) => {
    const msal = inject(MsalService);

    if (msal.instance.getAllAccounts()
        .length > 0) {
      return true;
    }

    msal.instance.loginRedirect({
      scopes: ['openid', 'profile',
        environment.b2cScope]
    });

    return false;
  };
```

Apply to all routes except login:

```typescript
// app.routes.ts — add canActivate to all routes
{
  path: 'dashboard',
  canActivate: [authGuard],
  loadComponent: ...
}
```

### Login Page

```typescript
// src/app/features/auth/login.component.ts
@Component({
  selector: 'app-login',
  standalone: true,
  imports: [MatButtonModule],
  template: `
    <div class="login-container">
      <mat-card class="login-card">
        <h1>SwingTrader</h1>
        <p>Autonomous swing trading system</p>
        <button mat-raised-button color="primary"
          (click)="login()">
          Sign in with Google
        </button>
        <p class="disclaimer">
          Trading involves risk. This system
          is not financial advice.
        </p>
      </mat-card>
    </div>
  `
})
export class LoginComponent {
  private msal = inject(MsalService);

  login(): void {
    this.msal.loginRedirect({
      scopes: ['openid', 'profile',
        environment.b2cScope]
    });
  }
}
```

### Auth Service (Activate)

```typescript
// src/app/core/services/auth.service.ts
@Injectable({ providedIn: 'root' })
export class AuthService {
  private msal = inject(MsalService);
  private api = inject(ApiService);

  isAuthenticated = computed(() =>
    this.msal.instance.getAllAccounts()
      .length > 0);

  currentUser = computed(() => {
    const accounts =
      this.msal.instance.getAllAccounts();
    if (accounts.length === 0) return null;
    return {
      name: accounts[0].name,
      email: accounts[0].username,
      userId: accounts[0].localAccountId
    };
  });

  isAdmin = signal<boolean>(false);

  constructor() {
    // Check admin status on init
    if (this.isAuthenticated()) {
      this.api.getAdminMe().subscribe({
        next: () => this.isAdmin.set(true),
        error: () => this.isAdmin.set(false)
      });
    }
  }

  logout(): void {
    this.msal.logoutRedirect();
  }
}
```

### environment.prod.ts Tokens

```typescript
export const environment = {
  production: true,
  apiUrl: '',
  b2cClientId: '#{B2C_CLIENT_ID}#',
  b2cAuthority: '#{B2C_AUTHORITY}#',
  b2cDomain: '#{B2C_DOMAIN}#',
  b2cScope: '#{B2C_SCOPE}#'
};
```

deploy-api.yml already replaces these
tokens before Angular build.

---

## Step 6: Data Migration for Existing User

The AddMultiTenancy migration already
created a 'system' Account and pointed all
pre-existing rows at it via the AccountId
default. All that's left is linking your
own login to that Account as Owner.

After first sign-in with your Google account:

1. Find your B2C object ID:
   Azure Portal → B2C tenant
   → Users → your account → Object ID

2. Create your AppUser record, attached to
   the existing 'system' Account:

```sql
-- Run against Azure SQL

-- Find the system account's Id (created by
-- the migration — usually 1, verify first)
SELECT Id, Name FROM Accounts;

-- Create your AppUser record as Owner
-- of that account
INSERT INTO AppUsers
  (UserId, Email, DisplayName, AccountId,
   Role, FirstLoginAt, LastLoginAt,
   IsActive, IsOnboarded)
VALUES
  ('{your-b2c-object-id}',
   '{your-email}',
   '{your-name}',
   {system-account-id},
   'Owner',
   GETUTCDATE(), GETUTCDATE(),
   1, 1);
```

No other tables need updating — they
already carry AccountId = {system-account-id}
from the migration default, so your
pre-existing trades/watchlists/etc. show up
immediately once you're linked as Owner.

3. Add ADMIN_USER_ID to GitHub secrets:
   Value = your B2C object ID
   (this is a separate, global concept from
   AccountRole — see Step 7. Admin lets you
   see the /admin area across all accounts;
   AccountRole.Owner only controls invites
   within your own account)

4. To add a second person to your account
   (e.g. a partner or friend), use the
   invite flow instead of manual SQL — see
   Step 3's Invite Endpoints and the /join
   Angular route. They sign in with their
   own Google account and land in the same
   Account, as a Member.

---

## Step 7: Admin Middleware

```csharp
// SwingTrader.Api/Auth/AdminRequirement.cs
public class AdminRequirement
  : IAuthorizationRequirement { }

public class AdminHandler
  : AuthorizationHandler<AdminRequirement>
{
  private readonly string _adminUserId;

  public AdminHandler(
    IConfiguration configuration)
  {
    _adminUserId = configuration
      ["Admin:UserId"] ?? string.Empty;
  }

  protected override Task HandleRequirementAsync(
    AuthorizationHandlerContext context,
    AdminRequirement requirement)
  {
    var userId = context.User
      .FindFirst("sub")?.Value;
    if (userId == _adminUserId)
      context.Succeed(requirement);
    return Task.CompletedTask;
  }
}
```

Register in Program.cs:

```csharp
builder.Services.AddAuthorization(options =>
{
  options.AddPolicy("Admin",
    policy => policy.Requirements
      .Add(new AdminRequirement()));
});

builder.Services.AddSingleton<
  IAuthorizationHandler, AdminHandler>();
```

Protect admin endpoints:

```csharp
app.MapGroup("/api/admin")
  .RequireAuthorization("Admin")
  .MapGet("/me", () => Results.Ok(
    new { isAdmin = true }))
  .MapGet("/users", GetUsers)
  // ... etc
```

---

## Tests

### Repository Tests

All existing repository tests pass because
they use EF InMemory and a mock
IAccountContext:

```csharp
// In test setup, add:
var mockAccountContext =
  Substitute.For<IAccountContext>();
mockAccountContext.AccountId.Returns(1);
mockAccountContext.UserId.Returns("test-user-id");
mockAccountContext.Role.Returns(AccountRole.Owner);
services.AddSingleton(mockAccountContext);
```

### New Auth Tests

```
Test: UnauthenticatedRequest_Returns401
  Call GET /api/portfolio without token
  Assert 401 Unauthorized

Test: AuthenticatedRequest_Returns200
  Call GET /api/portfolio with valid JWT
  Assert 200

Test: DataIsolation_AccountACannotSeeAccountBData
  Create trades for account A
  Authenticate as a user belonging to
    account B
  Call GET /api/trades/recent
  Assert empty list

Test: NewLogin_CreatesNewAccountAsOwner
  Simulate first login with no invite token
  Assert new Account created
  Assert AppUser created with
    Role = Owner and AccountId pointing
    at the new Account
  Assert 22+ watchlist items created for
    that AccountId

Test: InviteAcceptance_JoinsExistingAccountAsMember
  Owner creates an invite for account A
  A different, brand-new login authenticates
    with the invite token attached
  Assert AppUser created with
    Role = Member and AccountId = A
    (not a new Account)
  Assert AccountInvite.AcceptedAt is set

Test: SharedAccountData_BothUsersSeeSameTrades
  Two AppUsers share AccountId A
    (one Owner, one Member)
  Owner creates a trade
  Member calls GET /api/trades/recent
  Assert the trade is visible

Test: MemberCannotCreateInvites
  Member calls POST /api/account/invites
  Assert 403 Forbidden

Test: AdminEndpoint_NonAdmin_Returns403
  Regular user calls GET /api/admin/stats
  Assert 403

Test: AdminEndpoint_Admin_Returns200
  Admin user calls GET /api/admin/stats
  Assert 200
```

---

## Deliverables

1. dotnet test — all tests green

2. Sign in with Google works:
   Navigate to app URL
   Login page displays
   Click "Sign in with Google"
   Redirected to Google OAuth
   After consent: returned to dashboard

3. Dashboard shows your data:
   No 401 errors in browser console
   Portfolio data visible
   Your name shown in sidenav

4. API rejects unauthenticated requests:
   curl https://{app-url}/api/portfolio
   → 401 Unauthorized

5. Second Google account (no invite) sees
   empty state:
   Sign in with a different, uninvited
     Google account
   A new, separate Account is created for it
   Dashboard shows no data
   Fully isolated from your account

6. Invite flow works end to end:
   As Owner, generate an invite link from
     Settings/Members
   Open the link in a private browser window
   Sign in with a different Google account
   Land on the dashboard seeing the SAME
     data as the Owner (shared account)
   Owner's members list shows the new Member

7. Non-owner cannot manage members:
   Sign in as the invited Member
   POST /api/account/invites → 403
   DELETE /api/account/members/{id} → 403

8. Health endpoints still public:
   /health returns 200 without token

9. Approval email links still work:
   Clicking approve link works
   without requiring login

10. Admin area accessible from your login:
    /admin loads and shows overview
      across all accounts
    Non-admin logins get 403

11. README updated:
    B2C setup instructions
    How to find your B2C object ID
    Data migration SQL commands
    How new accounts get created on first
      login, and how invites work
