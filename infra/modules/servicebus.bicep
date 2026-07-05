param name string
param location string
param tags object

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
  'risk-jobs'
  'refinement-jobs'
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

output namespaceName string = namespace.name
output fullyQualifiedNamespace string = '${name}.servicebus.windows.net'
