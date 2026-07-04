using '../main.bicep'

param environment = 'dev'
param location = 'westeurope'
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD')
param adminUserId = readEnvironmentVariable('ADMIN_USER_ID', '')
