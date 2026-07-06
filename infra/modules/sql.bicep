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

// sqlTier here is actually the SKU *name* ('Basic', 'S0', 'S1', 'P1', ...),
// not the SKU tier - Azure SQL's sku.tier is the service tier family
// ('Basic'/'Standard'/'Premium'), a distinct field from sku.name. Passing
// sqlTier directly into both name and tier meant a non-Basic value (e.g.
// 'S0') produced sku.tier = 'S0', which isn't a valid tier at all.
var sqlSkuTier = sqlTier == 'Basic' ? 'Basic' : (startsWith(sqlTier, 'P') ? 'Premium' : 'Standard')

resource database 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: sqlTier
    tier: sqlSkuTier
    capacity: sqlCapacity
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    requestedBackupStorageRedundancy: 'Local'
  }
}

output serverName string = sqlServer.name
output databaseName string = database.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName

// Deliberately no connectionString output here: a @secure() output is
// withheld entirely from 'az deployment group create' results (that's the
// whole point of marking it secure), which broke the workflow trying to
// read it. A non-secure output would embed the admin password in plain
// deployment history instead. Callers build the connection string
// themselves from serverFqdn/databaseName plus the SQL_ADMIN_PASSWORD
// secret they already hold.
