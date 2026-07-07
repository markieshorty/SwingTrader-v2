param name string
param location string
param appInsightsConnectionString string
param keyVaultUri string
param tags object

@description('Service Bus fully-qualified namespace - empty until Phase 10d deploys it')
param serviceBusNamespace string = ''

@description('Container App FQDN, for the KeepWarm timer to ping')
param apiFqdn string = ''

var storageAccountName = take(replace(name, '-', ''), 24)
var deploymentContainerName = 'app-package'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: deploymentContainerName
}

// Flex Consumption (FC1) — a separate SKU/quota family from the classic Y1
// Dynamic plan. Tried here because Y1 hit a subscription-level quota block
// in uksouth that self-service quota requests couldn't resolve; FC1 draws
// from its own pool and may already have capacity.
resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${name}-plan'
  location: location
  tags: tags
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0'
      }
    }
    siteConfig: {
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        { name: 'KeyVaultUrl', value: keyVaultUri }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        {
          // Double underscore = managed identity auth - no connection
          // string or shared access key needed.
          name: 'ServiceBusConnection__fullyQualifiedNamespace'
          value: serviceBusNamespace
        }
        { name: 'ApiBaseUrl', value: apiFqdn == '' ? '' : 'https://${apiFqdn}' }
        // The Angular SPA is served from the same origin as the API
        // (wwwroot static files), so this is the same URL - used to build
        // the /trades?tab=approvals link in the approval reminder email.
        // Without this, ApprovalConfig.BaseUrl fell back to its
        // localhost:5001 dev default, breaking the link in production.
        { name: 'Approval__BaseUrl', value: apiFqdn == '' ? '' : 'https://${apiFqdn}' }
      ]
    }
  }
}

output principalId string = functionApp.identity.principalId
output name string = functionApp.name
