param environmentName string
param appName string
param location string
param appInsightsConnectionString string

@description('ARM resource id of the App Insights component, for the admin monitoring dashboard LogsQueryClient. Empty disables the App Insights card (degrades gracefully) rather than failing to deploy.')
param appInsightsResourceId string = ''

param keyVaultUri string
param tags object

@description('Azure AD B2C authority URL - empty until Phase 10c manual B2C setup is complete')
param b2cAuthority string = ''

@description('Azure AD B2C API audience (App ID URI) - empty until Phase 10c manual B2C setup is complete')
param b2cAudience string = ''

@description('Service Bus fully-qualified namespace, for the manual /run/{jobType} trigger endpoints - empty disables them (503) rather than failing to deploy')
param serviceBusNamespace string = ''

@description('Custom domain for the app, e.g. app.example.com. Empty = no custom domain (default *.azurecontainerapps.io only).')
param appCustomDomain string = ''

@description('Bind the custom domain + issue a managed certificate. Only turn on AFTER the app CNAME + asuid.app TXT records resolve (DNS delegated), or cert issuance hangs. See docs/branding-and-domains.md.')
param bindAppCustomDomain bool = false

// Guard so a normal deploy (defaults) leaves ingress exactly as before.
var enableAppDomain = bindAppCustomDomain && !empty(appCustomDomain)

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
        // SNI-bound custom domain + Container Apps managed (free) certificate.
        // null when disabled, so the default deploy's ingress is unchanged.
        customDomains: enableAppDomain ? [
          {
            name: appCustomDomain
            bindingType: 'SniEnabled'
            certificateId: appManagedCert.id
          }
        ] : null
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
            {
              // Admin monitoring dashboard queries App Insights logs via
              // managed identity (Monitoring Reader role). Empty = the
              // dashboard's App Insights card reports unavailable rather than
              // erroring.
              name: 'ApplicationInsights__ResourceId'
              value: appInsightsResourceId
            }
            { name: 'AzureAdB2C__Authority', value: b2cAuthority }
            { name: 'AzureAdB2C__Audience', value: b2cAudience }
            {
              // Double underscore = managed identity auth - no connection
              // string or shared access key needed. Mirrors functionapp.bicep.
              name: 'ServiceBusConnection__fullyQualifiedNamespace'
              value: serviceBusNamespace
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

// Free managed certificate for the app custom domain. Validated by CNAME
// (app.<domain> → this app's default FQDN) plus the asuid.app TXT proof, so
// the DNS records must resolve before this is enabled or issuance hangs.
resource appManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (enableAppDomain) {
  parent: containerAppEnv
  name: 'cert-${uniqueString(appCustomDomain)}'
  location: location
  tags: tags
  properties: {
    subjectName: appCustomDomain
    domainControlValidation: 'CNAME'
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
// Publish as the asuid.app TXT record so Container Apps can verify domain
// ownership before binding the custom domain (dns.bicep containerAppAsuid).
output customDomainVerificationId string = containerApp.properties.customDomainVerificationId
