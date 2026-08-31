metadata description = 'User-assigned managed identities for the Vistara API, worker, and migration job.'

@description('Azure region for the identities.')
param location string

@description('Tags applied to every identity.')
param tags object

@description('Name of the identity used by the API container app.')
param apiIdentityName string

@description('Name of the identity used by the worker container app.')
param workerIdentityName string

@description('Name of the identity used by the migration job.')
param migrateIdentityName string

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: apiIdentityName
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: workerIdentityName
  location: location
  tags: tags
}

resource migrateIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: migrateIdentityName
  location: location
  tags: tags
}

output apiResourceId string = apiIdentity.id
output apiClientId string = apiIdentity.properties.clientId
output apiPrincipalId string = apiIdentity.properties.principalId

output workerResourceId string = workerIdentity.id
output workerClientId string = workerIdentity.properties.clientId
output workerPrincipalId string = workerIdentity.properties.principalId

output migrateResourceId string = migrateIdentity.id
output migrateClientId string = migrateIdentity.properties.clientId
output migratePrincipalId string = migrateIdentity.properties.principalId
