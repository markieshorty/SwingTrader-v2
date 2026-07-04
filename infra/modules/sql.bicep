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

// Server is provisioned with classic SQL auth (administratorLogin/Password)
// only - no Azure AD admin is configured - so the connection string must
// use SQL auth to match, not Active Directory Default. @secure() keeps the
// embedded password out of deployment history/portal output display.
@secure()
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${databaseName};User ID=sqladmin;Password=${adminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
