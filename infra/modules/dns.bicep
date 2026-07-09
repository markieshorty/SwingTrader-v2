// Azure DNS zone for the marketing + app domains. Create the zone first, then
// delegate your registrar's nameservers to the four Azure NS shown in this
// module's `nameServers` output (one-time, at the registrar). Records that
// depend on values only known after other resources exist (SWA hostname/IP,
// container-app FQDN + verification id, apex validation token) are each gated
// on their parameter being supplied, so this module can be applied in phases.
// See docs/branding-and-domains.md for the full sequence.
param rootDomain string
param tags object

@description('Static Web App default hostname (…azurestaticapps.net) for the www CNAME.')
param swaDefaultHostname string = ''

@description('Apex inbound IP address that Static Web Apps issues when the apex custom domain is added.')
param swaApexIp string = ''

@description('TXT validation token Static Web Apps issues for the apex custom domain (dns-txt-token).')
param swaApexValidationToken string = ''

@description('Container App ingress FQDN for the app.<domain> CNAME.')
param containerAppFqdn string = ''

@description('Container App custom-domain verification id, published as the asuid.app TXT record.')
param containerAppAsuid string = ''

resource zone 'Microsoft.Network/dnsZones@2018-05-01' = {
  name: rootDomain
  location: 'global'
  tags: tags
}

// www.<domain> → Static Web App (CNAME delegation validation + serving).
resource wwwCname 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = if (!empty(swaDefaultHostname)) {
  parent: zone
  name: 'www'
  properties: {
    TTL: 3600
    CNAMERecord: { cname: swaDefaultHostname }
  }
}

// Apex <domain> → Static Web App (A record to the SWA-issued inbound IP).
resource apexA 'Microsoft.Network/dnsZones/A@2018-05-01' = if (!empty(swaApexIp)) {
  parent: zone
  name: '@'
  properties: {
    TTL: 3600
    ARecords: [ { ipv4Address: swaApexIp } ]
  }
}

// Apex validation token for the SWA custom domain (dns-txt-token method).
resource apexTxt 'Microsoft.Network/dnsZones/TXT@2018-05-01' = if (!empty(swaApexValidationToken)) {
  parent: zone
  name: '@'
  properties: {
    TTL: 3600
    TXTRecords: [ { value: [ swaApexValidationToken ] } ]
  }
}

// app.<domain> → Container App (the authenticated application).
resource appCname 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = if (!empty(containerAppFqdn)) {
  parent: zone
  name: 'app'
  properties: {
    TTL: 3600
    CNAMERecord: { cname: containerAppFqdn }
  }
}

// Container App domain-ownership proof (asuid.app TXT = verification id).
resource appAsuidTxt 'Microsoft.Network/dnsZones/TXT@2018-05-01' = if (!empty(containerAppAsuid)) {
  parent: zone
  name: 'asuid.app'
  properties: {
    TTL: 3600
    TXTRecords: [ { value: [ containerAppAsuid ] } ]
  }
}

output zoneName string = zone.name
output nameServers array = zone.properties.nameServers
