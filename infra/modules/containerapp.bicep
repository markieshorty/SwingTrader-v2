param environmentName string
param appName string
param location string
param appInsightsConnectionString string
param keyVaultUri string
param tags object

// Bootstrap placeholder — no image has ever been pushed to ACR on a brand-new
// deploy, so pointing at the real ACR image tag here would hang/fail waiting
// on a pull that can never succeed. deploy-api.yml swaps this out
// imperatively via 'az containerapp update --image ...' once a real image
// exists; Bicep never needs to know the real tag or reference ACR at all.
// Must be an image that honors ASPNETCORE_URLS (set below to listen on
// targetPort 5001) rather than a fixed port — containerapps-helloworld
// hardcodes port 80, which left the ingress health check waiting forever
// against our port-5001 target and hung deployment.
var bootstrapImage = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: {
        external: true
        targetPort: 5001
        transport: 'http'
        allowInsecure: false
      }
      // No registries[] block on the initial deploy: Container Apps appears
      // to validate every configured registry credential during revision
      // provisioning, even when the current image isn't pulled from it. The
      // ACR-pull role assignment below is a separate resource in this same
      // template with no guaranteed ordering ahead of this validation, so
      // referencing ACR here caused revision provisioning to hang
      // indefinitely (Operation expired). deploy-api.yml registers ACR
      // credentials itself (az containerapp registry set) once a real image
      // exists and the role assignment has had time to propagate.
    }
    template: {
      containers: [
        {
          name: 'swingtrader-api'
          image: bootstrapImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:5001' }
            { name: 'KeyVaultUrl', value: keyVaultUri }
            {
              name: 'ApplicationInsights__ConnectionString'
              value: appInsightsConnectionString
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, appName, 'acrpull')
  scope: resourceGroup()
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output principalId string = containerApp.identity.principalId
output appName string = containerApp.name
output fqdn string = containerApp.properties.configuration.ingress.fqdn
