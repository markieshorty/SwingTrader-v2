using '../main.bicep'

param environment = 'prod'
param location = 'uksouth'
// sqlAdminPassword and adminUserId
// passed as GitHub secrets at deploy time
// never committed to repo
