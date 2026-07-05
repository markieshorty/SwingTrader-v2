param serviceBusNamespaceName string
param principalId string

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// Data Owner so the Functions app can both send (Scheduler) and receive
// (Consumers) without a connection string/shared access key.
resource sbRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace.id, principalId, 'sbowner')
  scope: namespace
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '090c5cfd-751d-490a-894a-3ce6f1109419')
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
