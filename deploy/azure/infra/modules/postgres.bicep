metadata description = 'PostgreSQL Flexible Server restricted to Microsoft Entra authentication.'

@description('Azure region for the server.')
param location string

@description('Tags applied to the server.')
param tags object

@description('Globally unique PostgreSQL Flexible Server name.')
@minLength(3)
@maxLength(63)
param name string

@description('Compute SKU, for example Standard_B1ms.')
param sku string = 'Standard_B1ms'

@description('Provisioned storage in GB.')
param storageGb int = 32

@description('Major PostgreSQL engine version.')
@allowed([
  '15'
  '16'
  '17'
])
param version string = '17'

@description('Application database created for Vistara.')
param databaseName string = 'vistara'

@description('Entra tenant that owns the server administrator.')
param entraTenantId string

@description('Object ID of the Entra principal that administers the server.')
param entraAdminObjectId string

@description('User principal name or display name of the Entra server administrator.')
param entraAdminPrincipalName string

@description('Directory object kind of the Entra server administrator.')
@allowed([
  'User'
  'Group'
  'ServicePrincipal'
])
param entraAdminPrincipalType string = 'User'

@description('Days of automated backup retention.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 7

@description('Apply a CanNotDelete lock so teardown keeps the server and its data.')
param retainData bool = true

var tier = startsWith(sku, 'Standard_B')
  ? 'Burstable'
  : (startsWith(sku, 'Standard_E') ? 'MemoryOptimized' : 'GeneralPurpose')

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
    tier: tier
  }
  properties: {
    version: version
    createMode: 'Default'
    // No administrator login or password exists: password authentication is
    // disabled, so every principal connects with an Entra token.
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: entraTenantId
    }
    storage: {
      storageSizeGB: storageGb
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource administrator 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2025-08-01' = {
  parent: server
  name: entraAdminObjectId
  properties: {
    principalName: entraAdminPrincipalName
    principalType: entraAdminPrincipalType
    tenantId: entraTenantId
  }
}

resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = {
  parent: server
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
  dependsOn: [
    administrator
  ]
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: server
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
  dependsOn: [
    allowAzureServices
  ]
}

resource serverLock 'Microsoft.Authorization/locks@2020-05-01' = if (retainData) {
  name: 'lock-${name}'
  scope: server
  properties: {
    level: 'CanNotDelete'
    notes: 'Vistara application data lives on this server. Run down.sh --delete-data to remove this lock.'
  }
}

output resourceId string = server.id
output name string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
