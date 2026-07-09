// Marketing site — an Azure Static Web App, deployed to via GitHub Actions with
// a deployment token (Azure/static-web-apps-deploy in deploy-marketing.yml),
// so it's created "detached" from any repo here.
//
// Custom domains are gated behind bindCustomDomains so the resource can be
// created first (getting its *.azurestaticapps.net hostname + the apex
// validation token) and the domains bound in a second pass once the Azure DNS
// zone is delegated at the registrar. See docs/branding-and-domains.md.
param name string
// SWA is only available in a handful of regions (and some, incl. westeurope,
// are closed to new customers). eastus2 accepts new sites; content is served
// from a global CDN regardless, so the region is just where metadata lives.
param location string = 'eastus2'
param tags object

@description('Apex/root domain, e.g. example.com. Empty disables all custom-domain resources.')
param rootDomain string = ''

@description('Bind the apex + www custom domains. Only turn on after the DNS zone is delegated so validation can complete.')
param bindCustomDomains bool = false

// Free supports up to 2 custom domains (apex + www), which is all a marketing
// site needs — no reason to pay for Standard here.
@allowed(['Free', 'Standard'])
param sku string = 'Free'

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku
  }
  properties: {
    allowConfigFileUpdates: true
  }
}

// Apex (example.com) — validated by a TXT token Azure generates for this
// custom-domain resource; dns.bicep publishes that token as the _dnsauth TXT.
resource apex 'Microsoft.Web/staticSites/customDomains@2023-12-01' = if (bindCustomDomains && !empty(rootDomain)) {
  parent: swa
  name: rootDomain
  properties: {
    validationMethod: 'dns-txt-token'
  }
}

// www — validated by CNAME delegation (the www CNAME in dns.bicep points here).
resource www 'Microsoft.Web/staticSites/customDomains@2023-12-01' = if (bindCustomDomains && !empty(rootDomain)) {
  parent: swa
  name: 'www.${rootDomain}'
  properties: {
    validationMethod: 'cname-delegation'
  }
}

output name string = swa.name
output defaultHostname string = swa.properties.defaultHostname
