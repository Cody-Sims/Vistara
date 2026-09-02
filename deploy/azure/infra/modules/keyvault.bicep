metadata description = 'RBAC-authorized Key Vault holding the Data Protection wrapping key and operator-managed secrets.'

@description('Azure region for the vault.')
param location string

@description('Tags applied to the vault.')
param tags object

@description('Globally unique Key Vault name.')
@minLength(3)
@maxLength(24)
param name string

@description('Entra tenant that owns the vault.')
param tenantId string

@description('Name of the RSA key that wraps the Data Protection key ring.')
param dataProtectionKeyName string = 'data-protection'

@description('Size of the Data Protection wrapping key.')
@allowed([
  2048
  3072
  4096
])
param dataProtectionKeySize int = 3072

@description('Days a soft-deleted vault stays recoverable.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

@description('Apply a CanNotDelete lock so teardown keeps the vault and its key material.')
param retainData bool = true

resource vault 'Microsoft.KeyVault/vaults@2026-02-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    // Data-plane access is granted through role assignments only; access
    // policies are never evaluated.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2026-02-01' = {
  parent: vault
  name: dataProtectionKeyName
  properties: {
    kty: 'RSA'
    keySize: dataProtectionKeySize
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
  }
}

resource vaultLock 'Microsoft.Authorization/locks@2020-05-01' = if (retainData) {
  name: 'lock-${name}'
  scope: vault
  properties: {
    level: 'CanNotDelete'
    notes: 'Deleting this vault orphans every Data Protection payload. Run down.sh --delete-data to remove this lock.'
  }
}

output resourceId string = vault.id
output name string = vault.name
output vaultUri string = vault.properties.vaultUri
output dataProtectionKeyId string = dataProtectionKey.properties.keyUri
