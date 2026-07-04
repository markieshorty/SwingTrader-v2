param keyVaultName string
param principalId string

@allowed(['SecretsUser', 'CryptoOfficer'])
param roleType string = 'SecretsUser'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

var roles = {
  SecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  CryptoOfficer: '14b46e9e-c2b7-41b4-b07b-48a6ebf60603'
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, roles[roleType])
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      roles[roleType])
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
