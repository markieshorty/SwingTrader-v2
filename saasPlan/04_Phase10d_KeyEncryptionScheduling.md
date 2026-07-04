# SwingTrader — Phase 10d
# Per-User Key Encryption and Queue-Based Scheduling

## Context
Phase 10c complete. Users sign in with Google.
Data isolated per user. All users still share
the single set of API keys (your Finnhub,
Tiingo, T212 keys).

Phase 10d fixes both gaps:
  1. Per-user encrypted API key storage
     using AES-256 + Azure Key Vault
  2. Service Bus queue-based scheduling
     so each user's agents run with
     their own API credentials
  3. Settings page fully implemented

---

## Step 1: Bicep — Service Bus

### infra/modules/servicebus.bicep (new file)

```bicep
param name string
param location string
param tags object
param functionsPrincipalId string

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}

var queues = [
  'research-jobs'
  'watchlist-jobs'
  'report-jobs'
  'execution-jobs'
  'monitor-jobs'
]

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = [
  for queueName in queues: {
    parent: namespace
    name: queueName
    properties: {
      maxDeliveryCount: 3
      lockDuration: 'PT5M'
      defaultMessageTimeToLive: 'P1D'
    }
  }
]

// Grant Function App Data Owner role
// so it can both send and receive
resource sbRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace.id, functionsPrincipalId, 'sbowner')
  scope: namespace
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '090c5cfd-751d-490a-894a-3ce6f1109419')
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output namespaceName string = namespace.name
output fullyQualifiedNamespace string =
  '${name}.servicebus.windows.net'
```

### Update infra/main.bicep

Add Service Bus module and Key Vault
crypto access:

```bicep
// Add Service Bus module
module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    name: '${prefix}-sb-${environment}'
    location: location
    tags: tags
    functionsPrincipalId: functions.outputs.principalId
  }
}

// Add crypto access for key encryption
// (Container App needs to wrap/unwrap keys)
module kvCryptoApi 'modules/keyvaultaccess.bicep' = {
  name: 'kvcrypto-api'
  params: {
    keyVaultName: keyVault.outputs.name
    principalId: containerApp.outputs.principalId
    roleType: 'CryptoOfficer'
  }
}

module kvCryptoFunctions 'modules/keyvaultaccess.bicep' = {
  name: 'kvcrypto-functions'
  params: {
    keyVaultName: keyVault.outputs.name
    principalId: functions.outputs.principalId
    roleType: 'CryptoOfficer'
  }
}

// Add output
output serviceBusNamespace string =
  serviceBus.outputs.fullyQualifiedNamespace
```

### Update infra/modules/functionapp.bicep

Add Service Bus connection using
managed identity (no connection string):

```bicep
// Add param:
param serviceBusNamespace string = ''

// Add to appSettings array:
{
  name: 'ServiceBusConnection__fullyQualifiedNamespace'
  value: serviceBusNamespace
  // Double underscore = managed identity auth
  // No connection string or shared access key
}
```

### Update infra/main.bicep (functions module call)

```bicep
module functions 'modules/functionapp.bicep' = {
  name: 'functions'
  params: {
    name: '${prefix}-functions-${environment}'
    location: location
    appInsightsConnectionString:
      appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    serviceBusNamespace:
      serviceBus.outputs.fullyQualifiedNamespace
    tags: tags
  }
}
```

Push infra/ changes → deploy-infra.yml
deploys Service Bus automatically.

---

## Step 2: Per-User API Key Encryption

### New Entity: UserApiKey

```csharp
// SwingTrader.Core/Entities/UserApiKey.cs
public class UserApiKey : BaseEntity
{
  public string UserId { get; set; }
  public string Provider { get; set; }
    // "Finnhub", "Tiingo", "Trading212Key",
    // "Trading212Secret", "Claude",
    // "EmailUsername", "EmailPassword"
  public string EncryptedValue { get; set; }
  public string EncryptedDek { get; set; }
  public bool IsValid { get; set; } = false;
  public DateTime? LastTestedAt { get; set; }
  public string? LastTestResult { get; set; }
}

public enum KeyStatus
{
  NotSet,
  SetNotTested,
  Valid,
  Invalid
}
```

### IKeyEncryptionService

```csharp
// SwingTrader.Core/Interfaces/
//   IKeyEncryptionService.cs
public interface IKeyEncryptionService
{
  Task<(string EncryptedValue,
        string EncryptedDek)> EncryptAsync(
    string userId,
    string plaintext,
    CancellationToken ct);

  Task<string> DecryptAsync(
    string userId,
    string encryptedValue,
    string encryptedDek,
    CancellationToken ct);
}
```

### KeyEncryptionService Implementation

```csharp
// SwingTrader.Infrastructure/Security/
//   KeyEncryptionService.cs
public class KeyEncryptionService
  : IKeyEncryptionService
{
  private readonly string _keyVaultUrl;

  public KeyEncryptionService(
    IConfiguration config)
  {
    _keyVaultUrl = config["KeyVaultUrl"]
      ?? throw new InvalidOperationException(
        "KeyVaultUrl not configured");
  }

  public async Task<(string, string)>
    EncryptAsync(
      string userId,
      string plaintext,
      CancellationToken ct)
  {
    // 1. Generate random AES-256 DEK
    var dek = new byte[32];
    var iv = new byte[16];
    RandomNumberGenerator.Fill(dek);
    RandomNumberGenerator.Fill(iv);

    // 2. Encrypt plaintext with DEK
    using var aes = Aes.Create();
    aes.Key = dek;
    aes.IV = iv;
    var encryptor = aes.CreateEncryptor();
    var plaintextBytes = Encoding.UTF8
      .GetBytes(plaintext);
    var encrypted = encryptor
      .TransformFinalBlock(
        plaintextBytes, 0,
        plaintextBytes.Length);

    // Combine IV + ciphertext
    var combined = iv.Concat(encrypted).ToArray();
    var encryptedValue = Convert
      .ToBase64String(combined);

    // 3. Wrap DEK with Key Vault key
    var keyName = GetKeyName(userId);
    await EnsureKeyExistsAsync(keyName, ct);

    var cryptoClient = GetCryptoClient(keyName);
    var wrapResult = await cryptoClient
      .WrapKeyAsync(
        KeyWrapAlgorithm.RsaOaep, dek, ct);
    var encryptedDek = Convert
      .ToBase64String(wrapResult.EncryptedKey);

    // Clear DEK from memory
    Array.Clear(dek, 0, dek.Length);

    return (encryptedValue, encryptedDek);
  }

  public async Task<string> DecryptAsync(
    string userId,
    string encryptedValue,
    string encryptedDek,
    CancellationToken ct)
  {
    // 1. Unwrap DEK from Key Vault
    var keyName = GetKeyName(userId);
    var cryptoClient = GetCryptoClient(keyName);

    var encryptedDekBytes = Convert
      .FromBase64String(encryptedDek);
    var unwrapResult = await cryptoClient
      .UnwrapKeyAsync(
        KeyWrapAlgorithm.RsaOaep,
        encryptedDekBytes, ct);
    var dek = unwrapResult.Key;

    // 2. Decrypt with DEK
    var combined = Convert
      .FromBase64String(encryptedValue);
    var iv = combined[..16];
    var ciphertext = combined[16..];

    using var aes = Aes.Create();
    aes.Key = dek;
    aes.IV = iv;
    var decryptor = aes.CreateDecryptor();
    var decrypted = decryptor
      .TransformFinalBlock(
        ciphertext, 0, ciphertext.Length);

    Array.Clear(dek, 0, dek.Length);

    return Encoding.UTF8.GetString(decrypted);
  }

  private static string GetKeyName(
    string userId) =>
    $"user-{userId.Replace("-", "")}-key";

  private CryptographyClient GetCryptoClient(
    string keyName) =>
    new CryptographyClient(
      new Uri($"{_keyVaultUrl}keys/{keyName}"),
      new DefaultAzureCredential());

  private async Task EnsureKeyExistsAsync(
    string keyName, CancellationToken ct)
  {
    var keyClient = new KeyClient(
      new Uri(_keyVaultUrl),
      new DefaultAzureCredential());
    try
    {
      await keyClient.GetKeyAsync(keyName,
        cancellationToken: ct);
    }
    catch (RequestFailedException ex)
      when (ex.Status == 404)
    {
      await keyClient.CreateRsaKeyAsync(
        new CreateRsaKeyOptions(keyName)
        {
          KeySize = 2048,
          KeyOperations = {
            KeyOperation.WrapKey,
            KeyOperation.UnwrapKey
          }
        }, ct);
    }
  }
}
```

### IUserKeyService

```csharp
// SwingTrader.Core/Interfaces/IUserKeyService.cs
public interface IUserKeyService
{
  Task<string> GetKeyAsync(
    string userId,
    string provider,
    CancellationToken ct);

  Task SaveKeyAsync(
    string userId,
    string provider,
    string plaintext,
    CancellationToken ct);

  Task<bool> TestKeyAsync(
    string userId,
    string provider,
    CancellationToken ct);

  Task<Dictionary<string, KeyStatus>>
    GetKeyStatusesAsync(
      string userId,
      CancellationToken ct);
}
```

Implementation in SwingTrader.Agents/Keys/:
  Delegates encrypt/decrypt to IKeyEncryptionService
  Delegates storage to IUserApiKeyRepository
  For "Claude": falls back to shared key if
    user hasn't provided their own

### Per-User HTTP Client Factory

```csharp
// SwingTrader.Infrastructure/Http/
//   IUserHttpClientFactory.cs
public interface IUserHttpClientFactory
{
  Task<IFinnhubClient> CreateFinnhubAsync(
    string userId, CancellationToken ct);
  Task<ITiingoClient> CreateTiingoAsync(
    string userId, CancellationToken ct);
  Task<ITrading212Client> CreateT212Async(
    string userId, CancellationToken ct);
  Task<IClaudeClient> CreateClaudeAsync(
    string userId, CancellationToken ct);
}

// Implementation: for each client,
// gets user's key via IUserKeyService,
// creates a Refit client with a custom
// DelegatingHandler that injects the key
```

Each agent that previously injected
IFinnhubClient directly now injects
IUserHttpClientFactory and creates
a client per operation:

```csharp
var finnhub = await _clientFactory
  .CreateFinnhubAsync(userId, ct);
var quote = await finnhub
  .GetQuoteAsync(symbol);
```

### Migration: AddUserApiKeys

```bash
dotnet ef migrations add AddUserApiKeys \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

---

## Step 3: Service Bus Queue-Based Scheduling

Replace individual timer-triggered functions
with a scheduler pattern:

- SchedulerFunction (timer, every 5 min)
  checks what jobs need to run and
  enqueues messages per user

- Consumer functions (Service Bus trigger)
  process one user's job at a time
  with their own decrypted API keys

### New Entity: JobLogEntry

```csharp
// SwingTrader.Core/Entities/JobLogEntry.cs
public class JobLogEntry : BaseEntity
{
  public string UserId { get; set; }
  public string JobType { get; set; }
  public DateOnly JobDate { get; set; }
  public JobStatus Status { get; set; }
  public DateTime EnqueuedAt { get; set; }
  public DateTime? CompletedAt { get; set; }
  public string? ErrorMessage { get; set; }
  public int AttemptCount { get; set; } = 1;
}
```

### Job Message Records

```csharp
// SwingTrader.Core/Models/JobMessages.cs
public record ResearchJobMessage(
  string UserId,
  string JobId,
  DateOnly TradeDate,
  DateTime ScheduledFor);

public record WatchlistJobMessage(
  string UserId,
  string JobId,
  DateTime ScheduledFor);

public record ReportJobMessage(
  string UserId,
  string JobId,
  DateOnly ReportDate);

public record ExecutionJobMessage(
  string UserId,
  string JobId,
  DateOnly TradeDate);

public record MonitorJobMessage(
  string UserId,
  string JobId,
  DateTime CycleTime);
```

### SchedulerFunction (replaces individual timers)

```csharp
// SwingTrader.Functions/Functions/
//   SchedulerFunction.cs
public class SchedulerFunction
{
  [Function("Scheduler")]
  public async Task Run(
    [TimerTrigger("0 */5 * * * *")]
      TimerInfo timer,
    CancellationToken ct)
  {
    var nowUtc = DateTime.UtcNow;
    var nowEt = TimeZoneInfo
      .ConvertTimeFromUtc(nowUtc,
        EasternTimeZone);
    var today = DateOnly.FromDateTime(nowEt);

    // Skip if market not open today
    var users = await GetActiveUsersAsync(ct);

    foreach (var user in users)
    {
      try
      {
        await TryEnqueueResearchAsync(
          user, today, nowEt, ct);
        await TryEnqueueWatchlistAsync(
          user, nowEt, ct);
        await TryEnqueueReportAsync(
          user, today, nowEt, ct);
        await TryEnqueueExecutionAsync(
          user, today, nowEt, ct);
        await TryEnqueueMonitorAsync(
          user, today, nowEt, ct);
        await TryEnqueueRiskAsync(
          user, today, nowEt, ct);
        await TryEnqueueRefinementAsync(
          user, today, nowEt, ct);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex,
          "Scheduler failed for user {UserId}",
          user.UserId);
        // Continue to next user
      }
    }
  }

  private bool IsTimeWindow(
    DateTime nowEt,
    int startHour, int startMin,
    int endHour, int endMin)
  {
    var start = nowEt.Date
      .AddHours(startHour).AddMinutes(startMin);
    var end = nowEt.Date
      .AddHours(endHour).AddMinutes(endMin);
    return nowEt >= start && nowEt < end;
  }

  // Time windows (ET):
  // Research:   06:00-06:05 weekdays
  // Watchlist:  20:00-20:05 Sunday
  // Report:     06:30-06:35 weekdays
  // Execution:  09:20-09:25 weekdays
  // Monitor:    09:30-16:00 weekdays
  // Risk:       09:00-09:05 1st of month
  // Refinement: 08:00-08:05 15th of month

  // Each TryEnqueue checks:
  // - correct time window
  // - market day (where applicable)
  // - user not suspended
  // - not already enqueued today (JobLog)
  // Then sends Service Bus message
  // and marks enqueued in JobLog
}
```

### ResearchConsumerFunction (example)

```csharp
// SwingTrader.Functions/Functions/
//   ResearchConsumerFunction.cs
public class ResearchConsumerFunction
{
  [Function("ResearchConsumer")]
  public async Task Run(
    [ServiceBusTrigger(
      "research-jobs",
      Connection = "ServiceBusConnection")]
      ResearchJobMessage message,
    CancellationToken ct)
  {
    _logger.LogInformation(
      "Research starting for {UserId} {Date}",
      message.UserId, message.TradeDate);

    await _jobLog.MarkProcessingAsync(
      message.UserId, "Research",
      message.TradeDate, ct);

    try
    {
      // Set user context for repositories
      var userContext = _serviceProvider
        .GetRequiredService<FunctionUserContext>();
      userContext.UserId = message.UserId;

      // Create per-user HTTP clients
      var finnhub = await _clientFactory
        .CreateFinnhubAsync(
          message.UserId, ct);
      var tiingo = await _clientFactory
        .CreateTiingoAsync(
          message.UserId, ct);
      var claude = await _clientFactory
        .CreateClaudeAsync(
          message.UserId, ct);

      // Run research pipeline with user's clients
      var symbols = await _watchlistRepo
        .GetAllEnabledSymbolsAsync(ct);

      var semaphore = new SemaphoreSlim(3);
      var tasks = symbols.Select(async s =>
      {
        await semaphore.WaitAsync(ct);
        try
        {
          await _pipeline.RunAsync(
            s.Symbol, finnhub, tiingo,
            claude, ct);
        }
        finally { semaphore.Release(); }
      });

      await Task.WhenAll(tasks);

      await _jobLog.MarkCompletedAsync(
        message.UserId, "Research",
        message.TradeDate, ct);
    }
    catch (Exception ex)
    {
      await _jobLog.MarkFailedAsync(
        message.UserId, "Research",
        message.TradeDate, ex.Message, ct);
      throw;
      // Re-throw → Service Bus dead-letters
      // after maxDeliveryCount retries
    }
  }
}
```

Implement the same pattern for:
  WatchlistConsumerFunction
  ReportConsumerFunction
  ExecutionConsumerFunction
  MonitorConsumerFunction
  RiskConsumerFunction
  RefinementConsumerFunction

### Migration: AddJobLog

```bash
dotnet ef migrations add AddJobLog \
  --project SwingTrader.Data \
  --startup-project SwingTrader.Api
```

---

## Step 4: Settings Page

### Settings API Endpoints

```csharp
// /api/keys
app.MapGroup("/api/keys")
  .RequireAuthorization()
  .MapGet("/", GetKeyStatuses)
  .MapPost("/{provider}", SaveKey)
  .MapGet("/{provider}/test", TestKey)
  .MapDelete("/{provider}", DeleteKey);

// /api/user
app.MapGroup("/api/user")
  .RequireAuthorization()
  .MapGet("/profile", GetProfile)
  .MapPut("/trading-config", UpdateTradingConfig)
  .MapPut("/notification-config",
    UpdateNotificationConfig)
  .MapPost("/complete-onboarding",
    CompleteOnboarding)
  .MapDelete("/", DeleteAccount);
```

GetKeyStatuses response — never returns
actual key values, only status:

```json
{
  "Finnhub": "Valid",
  "Tiingo": "SetNotTested",
  "Trading212Key": "NotSet",
  "Trading212Secret": "NotSet",
  "Claude": "Valid",
  "EmailUsername": "Valid",
  "EmailPassword": "Valid"
}
```

### Angular Settings Page

Replace the Phase 10b placeholder with
full implementation.

Tabs:
  [API Keys] [Trading] [Notifications] [Account]

#### API Keys Tab

```typescript
// settings/api-keys/api-keys.component.ts
// For each provider, show a row:
// Provider name | Status badge | [Replace] [Test]

// Replace opens a dialog:
// - Password input field
// - "Keys are encrypted and never stored in plaintext"
// - [Save and Test] button
// - Shows result before closing

// Status badges:
// NotSet → grey "Not configured"
// SetNotTested → amber "Saved — not tested"
// Valid → green "✓ Connected"
// Invalid → red "✗ Connection failed"
```

#### Trading Tab

```typescript
// T212 mode toggle (Demo / Live)
// Approval required toggle
// Account ID display (read-only)
// Warning if Live mode selected
```

#### Notifications Tab

```typescript
// List of email addresses
// Add / remove recipients
// Per-address category selection
// (Daily report, Execution, Position closed,
//  Circuit breaker, Monthly summary)
```

#### Account Tab

```typescript
// User name and email from Google
// Member since date
// [Sign out]
// [Delete account] (with confirmation dialog)
// Global refinement opt-in toggle
//   (placeholder until Phase 10f)
```

---

## Step 5: DI Registration

### SwingTrader.Api and SwingTrader.Functions

```csharp
// Key encryption
services.AddScoped<IKeyEncryptionService,
  KeyEncryptionService>();
services.AddScoped<IUserKeyService,
  UserKeyService>();
services.AddScoped<IUserApiKeyRepository,
  UserApiKeyRepository>();

// Per-user HTTP client factory
services.AddScoped<IUserHttpClientFactory,
  UserHttpClientFactory>();

// Job log
services.AddScoped<IJobLogRepository,
  JobLogRepository>();

// FunctionUserContext (Functions only)
services.AddScoped<FunctionUserContext>();
services.AddScoped<IUserContext>(sp =>
  sp.GetRequiredService<FunctionUserContext>());
```

---

## Tests

### Encryption Tests

```
Test: EncryptDecrypt_RoundTrip
  Encrypt "test-api-key"
  Decrypt result
  Assert plaintext matches

Test: EncryptedValue_IsNotPlaintext
  Assert encrypted != "test-api-key"

Test: DifferentUsers_DifferentEncryption
  Encrypt same value for user A and B
  Assert results differ

Test: WrongUser_CannotDecrypt
  Encrypt for user A
  Decrypt as user B
  Assert exception
```

### Key Status Tests

```
Test: SaveKey_StoresEncrypted
  Save key for provider
  Read raw DB row
  Assert EncryptedValue != plaintext

Test: GetStatuses_NeverReturnsValues
  GET /api/keys
  Assert no "value" field in response
  Assert only status strings present

Test: TestKey_ValidKey_ReturnsValid
  User has valid Finnhub key
  GET /api/keys/Finnhub/test
  Assert { valid: true }
```

### Scheduler Tests

```
Test: Scheduler_EnqueuesResearch_AtCorrectTime
  Mock time = 6:02 AM ET weekday
  Assert research job enqueued per user

Test: Scheduler_SkipsSuspendedUsers
  User is suspended
  Assert no job enqueued for that user

Test: Scheduler_Idempotent_NoDoubleEnqueue
  Scheduler fires twice in same window
  Assert only one job per user per type

Test: Consumer_SetsUserContext
  Send research job for user A
  Assert pipeline uses user A's context

Test: Consumer_UsesPerUserKeys
  User A and B have different Finnhub keys
  Both research jobs run
  Assert each uses their own key

Test: Consumer_FailedJob_MarksFailedInLog
  Pipeline throws exception
  Assert JobLog status = Failed
  Assert error message stored
```

---

## Deliverables

1. dotnet test — all tests green

2. Bicep deploys cleanly:
   Service Bus namespace visible in portal
   5 queues visible:
     research-jobs, watchlist-jobs,
     report-jobs, execution-jobs,
     monitor-jobs

3. Settings page works:
   API Keys tab renders
   Enter Finnhub key → shows Valid ✓
   Enter wrong key → shows Invalid ✗
   Key stored (verify in DB:
     EncryptedValue column is ciphertext,
     not the actual key)

4. Keys isolated between users:
   User A sets their Finnhub key
   User B sets their Finnhub key
   Each research run uses their own key
   (verify in Application Insights logs)

5. Scheduler enqueues jobs:
   At 6am ET weekday: research-jobs queue
   shows messages in Azure portal

6. Consumer functions process jobs:
   Messages disappear from queue after
   successful processing
   JobLog table shows Completed entries

7. Failed jobs dead-lettered:
   Force a failure (temporarily invalid key)
   Message moves to dead-letter queue
   after 3 delivery attempts
   JobLog shows Failed status

8. Trading tab saves T212 mode:
   Toggle Demo/Live
   Confirm mode persists after refresh

9. Notifications tab:
   Add a second email address
   Confirm it receives the next report

10. README updated:
    How key encryption works
    How to rotate a user's API key
    How to check job status in Service Bus
