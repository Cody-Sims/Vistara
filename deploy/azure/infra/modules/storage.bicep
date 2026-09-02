metadata description = 'StorageV2 account with private media and Data Protection blob containers.'

@description('Azure region for the storage account.')
param location string

@description('Tags applied to the storage account.')
param tags object

@description('Globally unique storage account name.')
@minLength(3)
@maxLength(24)
param name string

@description('Storage redundancy SKU.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_GZRS'
  'Standard_RAGRS'
  'Standard_RAGZRS'
])
param storageRedundancy string = 'Standard_LRS'

@description('Private blob container that holds Vistara media.')
param mediaContainerName string = 'media'

@description('Private blob container that holds the ASP.NET Core Data Protection key ring.')
param dataProtectionContainerName string = 'dataprotection'

@description('Blob that holds the Data Protection key ring XML document.')
param dataProtectionBlobName string = 'keys.xml'

@description('Days a soft-deleted blob stays recoverable.')
@minValue(1)
@maxValue(365)
param blobSoftDeleteRetentionDays int = 7

@description('Apply a CanNotDelete lock so teardown keeps the account and its data.')
param retainData bool = true

resource storageAccount 'Microsoft.Storage/storageAccounts@2026-04-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: storageRedundancy
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    // Managed identity is the only supported credential: no account key and no
    // SAS token is ever issued for this account.
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
    isHnsEnabled: false
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: false
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
      }
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-04-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteRetentionDays
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteRetentionDays
    }
  }
}

resource mediaContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-04-01' = {
  parent: blobService
  name: mediaContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-04-01' = {
  parent: blobService
  name: dataProtectionContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource storageLock 'Microsoft.Authorization/locks@2020-05-01' = if (retainData) {
  name: 'lock-${name}'
  scope: storageAccount
  properties: {
    level: 'CanNotDelete'
    notes: 'Vistara media and Data Protection keys are retained by default. Run down.sh --delete-data to remove this lock.'
  }
}

output resourceId string = storageAccount.id
output name string = storageAccount.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output mediaContainerName string = mediaContainer.name
output dataProtectionContainerName string = dataProtectionContainer.name
output dataProtectionBlobUri string = '${storageAccount.properties.primaryEndpoints.blob}${dataProtectionContainerName}/${dataProtectionBlobName}'
