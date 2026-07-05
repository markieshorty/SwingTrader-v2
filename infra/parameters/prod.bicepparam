using '../main.bicep'

param environment = 'prod'
param location = 'uksouth'
// Sourced from environment variables the workflow sets from GitHub secrets —
// .bicepparam requires every non-defaulted param to be assigned here; it
// can't be filled in later via a supplemental `--parameters` CLI flag.
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD')
param adminUserId = readEnvironmentVariable('ADMIN_USER_ID', '')
param b2cAuthority = readEnvironmentVariable('B2C_AUTHORITY', '')
param b2cClientId = readEnvironmentVariable('B2C_CLIENT_ID', '')
