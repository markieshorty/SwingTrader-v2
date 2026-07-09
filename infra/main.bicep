targetScope = 'resourceGroup'

@description('Environment name')
param environment string = 'prod'

@description('Azure region')
param location string = resourceGroup().location

@secure()
param sqlAdminPassword string

// Accepted here so deploy-infra.yml can pass it through; consumed once
// Phase 10c populates the Admin:UserId Key Vault secret.
#disable-next-line no-unused-params
param adminUserId string = ''

@description('Azure AD B2C authority URL - empty until Phase 10c manual B2C setup is complete')
param b2cAuthority string = ''

@description('Azure AD B2C application client ID - empty until Phase 10c manual B2C setup is complete')
param b2cAudience string = ''

// ── Marketing site + custom domains (all default-off so a normal deploy is a
// no-op for these). Roll out in phases — see docs/branding-and-domains.md:
//   1. deployMarketing=true                     → create the marketing SWA
//   2. rootDomain='example.com'                 → create the DNS zone + app
//      CNAME/asuid + www CNAME records, then delegate nameservers at registrar
//   3. bindCustomDomains=true (+ swaApex* vals) → bind apex/www to the SWA and
//      app.<domain> to the Container App, issuing managed certs
@description('Create the marketing Static Web App.')
param deployMarketing bool = false

@description('Apex/root domain, e.g. example.com. Empty disables the DNS zone and all custom domains.')
param rootDomain string = ''

@description('Phase 3: bind apex/www (SWA) and app.<domain> (Container App) custom domains + issue certs. Only after DNS is delegated.')
param bindCustomDomains bool = false

@description('Phase 3: apex inbound IP that Static Web Apps issues once the apex custom domain is added.')
param swaApexIp string = ''

@description('Phase 3: apex TXT validation token Static Web Apps issues for the apex custom domain.')
param swaApexValidationToken string = ''

var prefix = 'swingtrader'
var appDomain = empty(rootDomain) ? '' : 'app.${rootDomain}'
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

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    name: '${prefix}-sb-${environment}'
    location: location
    tags: tags
  }
}

module containerApp 'modules/containerapp.bicep' = {
  name: 'containerapp'
  params: {
    environmentName: '${prefix}-env-${environment}'
    appName: '${prefix}-api-${environment}'
    location: location
    appInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    b2cAuthority: b2cAuthority
    b2cAudience: b2cAudience
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
    appCustomDomain: appDomain
    bindAppCustomDomain: bindCustomDomains
    tags: tags
  }
}

// Marketing site (sentrytrading.co.uk / www) — separate Static Web App.
module marketing 'modules/staticwebapp.bicep' = if (deployMarketing) {
  name: 'marketing'
  params: {
    name: '${prefix}-marketing-${environment}'
    tags: tags
    rootDomain: rootDomain
    bindCustomDomains: bindCustomDomains
  }
}

// Azure DNS zone for the domain (apex + www → marketing SWA, app → Container
// App). Delegate your registrar's nameservers to this zone's `nameServers`
// output. Records that need post-create values (SWA hostname/IP, apex token)
// are supplied via params as those become known.
module dns 'modules/dns.bicep' = if (!empty(rootDomain)) {
  name: 'dns'
  params: {
    rootDomain: rootDomain
    tags: tags
    // Guarded by the same flag as the marketing module's own condition.
    #disable-next-line BCP318
    swaDefaultHostname: deployMarketing ? marketing.outputs.defaultHostname : ''
    swaApexIp: swaApexIp
    swaApexValidationToken: swaApexValidationToken
    containerAppFqdn: containerApp.outputs.fqdn
    containerAppAsuid: containerApp.outputs.customDomainVerificationId
  }
}

module functions 'modules/functionapp.bicep' = {
  name: 'functions'
  params: {
    name: '${prefix}-functions-${environment}'
    location: location
    appInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
    apiFqdn: containerApp.outputs.fqdn
    tags: tags
  }
}

module serviceBusAccess 'modules/servicebusaccess.bicep' = {
  name: 'servicebusaccess'
  params: {
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    principalId: functions.outputs.principalId
  }
}

// The API's manual /run/{jobType} endpoints only ever send onto these
// queues (the Consumer Functions above receive), but reuses the same
// Data Owner grant as the Functions app rather than a narrower Sender-only
// module - one role definition to maintain instead of two.
module serviceBusAccessApi 'modules/servicebusaccess.bicep' = {
  name: 'servicebusaccess-api'
  params: {
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    principalId: containerApp.outputs.principalId
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

// Both the API (encrypting new keys) and Functions (decrypting them for
// consumer jobs) need to wrap/unwrap DEKs via Key Vault crypto operations.
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

output acrLoginServer string = acr.outputs.loginServer
output containerAppName string = containerApp.outputs.appName
output containerAppFqdn string = containerApp.outputs.fqdn
output functionAppName string = functions.outputs.name
output keyVaultName string = keyVault.outputs.name
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output serviceBusNamespace string = serviceBus.outputs.fullyQualifiedNamespace
// Marketing SWA default hostname (target for the www CNAME) and the Azure
// nameservers to delegate the domain to at your registrar. Each ternary is
// guarded by the same flag as the module it reads from.
#disable-next-line BCP318
output marketingHostname string = deployMarketing ? marketing.outputs.defaultHostname : ''
#disable-next-line BCP318
output dnsNameServers array = empty(rootDomain) ? [] : dns.outputs.nameServers
