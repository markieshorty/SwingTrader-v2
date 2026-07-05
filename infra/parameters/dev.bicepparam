using '../main.bicep'

param environment = 'dev'
param location = 'uksouth'
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD')
param adminUserId = readEnvironmentVariable('ADMIN_USER_ID', '')
param b2cAuthority = readEnvironmentVariable('B2C_AUTHORITY', '')
param b2cAudience = readEnvironmentVariable('B2C_AUDIENCE', '')
