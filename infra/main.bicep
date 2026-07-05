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
    appInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    b2cAuthority: b2cAuthority
    b2cAudience: b2cAudience
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

module functions 'modules/functionapp.bicep' = {
  name: 'functions'
  params: {
    name: '${prefix}-functions-${environment}'
    location: location
    appInsightsConnectionString: appInsights.outputs.connectionString
    keyVaultUri: keyVault.outputs.uri
    serviceBusNamespace: serviceBus.outputs.fullyQualifiedNamespace
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
