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
  'refinement-jobs'
  'candlesync-jobs'
  'backtest-jobs'
  'filingsync-jobs'
  'bellwether-jobs'
]

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = [
  for queueName in queues: {
    parent: namespace
    name: queueName
    properties: {
      // 10 (was 3) for job queues: Flex Consumption deploys/scale-ins abandon
      // in-flight locks, and 30-60 min consumers burned all 3 deliveries on
      // instance churn alone (watchlist DLQ'd 20 Jul 2026). Monitor stays at
      // 3 - a stale tick is superseded by the next one within 5 minutes.
      maxDeliveryCount: queueName == 'monitor-jobs' ? 3 : 10
      lockDuration: 'PT5M'
      defaultMessageTimeToLive: 'P1D'
    }
  }
]

output namespaceName string = namespace.name
output fullyQualifiedNamespace string = '${name}.servicebus.windows.net'
