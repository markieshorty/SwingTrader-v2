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
