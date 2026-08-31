metadata description = 'Least-privilege role assignments for the API and worker identities over the media container, the Data Protection container, and the vault.'

@description('Name of the storage account that holds the media and Data Protection containers.')
param storageAccountName string

@description('Private blob container that holds Vistara media.')
param mediaContainerName string

@description('Private blob container that holds the Data Protection key ring.')
param dataProtectionContainerName string

@description('Name of the Key Vault that holds the Data Protection key and operator-managed secrets.')
param keyVaultName string

@description('Principal ID of the API user-assigned identity.')
param apiPrincipalId string

@description('Principal ID of the worker user-assigned identity.')
param workerPrincipalId string

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var keyVaultCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource storageAccount 'Microsoft.Storage/storageAccounts@2026-04-01' existing = {
  name: storageAccountName

  resource blobService 'blobServices' existing = {
    name: 'default'

    resource mediaContainer 'containers' existing = {
      name: mediaContainerName
    }

    resource dataProtectionContainer 'containers' existing = {
      name: dataProtectionContainerName
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2026-02-01' existing = {
  name: keyVaultName
}

resource apiMediaBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(
    storageAccount::blobService::mediaContainer.id,
    apiPrincipalId,
    storageBlobDataContributorRoleId
  )
  scope: storageAccount::blobService::mediaContainer
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerMediaBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(
    storageAccount::blobService::mediaContainer.id,
    workerPrincipalId,
    storageBlobDataContributorRoleId
  )
  scope: storageAccount::blobService::mediaContainer
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiDataProtectionBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(
    storageAccount::blobService::dataProtectionContainer.id,
    apiPrincipalId,
    storageBlobDataContributorRoleId
  )
  scope: storageAccount::blobService::dataProtectionContainer
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiKeyVaultCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apiPrincipalId, keyVaultCryptoUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      keyVaultCryptoUserRoleId
    )
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apiPrincipalId, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      keyVaultSecretsUserRoleId
    )
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, workerPrincipalId, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      keyVaultSecretsUserRoleId
    )
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output roleAssignmentIds array = [
  apiMediaBlobContributor.id
  workerMediaBlobContributor.id
  apiDataProtectionBlobContributor.id
  apiKeyVaultCryptoUser.id
  apiKeyVaultSecretsUser.id
  workerKeyVaultSecretsUser.id
]
