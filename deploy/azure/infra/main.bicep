targetScope = 'subscription'

metadata description = 'Vistara hosted evaluation deployment: Container Apps, PostgreSQL Flexible Server, Storage, Key Vault, and cost controls in a single resource group.'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('azd environment name. Used as the azd-env-name tag and as the readable part of every resource name.')
@minLength(2)
@maxLength(20)
param environmentName string

@description('Azure region that receives every resource.')
@minLength(1)
param location string

@description('Resource group that receives every Vistara resource.')
@minLength(1)
@maxLength(90)
param resourceGroupName string = 'rg-vistara-${environmentName}'

@description('Digest-pinned API image, for example ghcr.io/<namespace>/vistara-api@sha256:<digest>. Tags are rejected.')
@minLength(1)
param apiImage string

@description('Digest-pinned worker image, for example ghcr.io/<namespace>/vistara-worker@sha256:<digest>. Tags are rejected.')
@minLength(1)
param workerImage string

@description('Digest-pinned migration image, for example ghcr.io/<namespace>/vistara-migrations@sha256:<digest>. Tags are rejected.')
@minLength(1)
param migrationImage string

@description('Container registry host serving the three images.')
param registryServer string = 'ghcr.io'

@description('Registry user name. Leave empty for a public registry.')
param registryUsername string = ''

@description('Key Vault secret URI holding the registry password. Never a password value, and only used for a private registry.')
param registryPasswordSecretUri string = ''

@description('Entra tenant that owns sign-in, the PostgreSQL administrator, and the Key Vault.')
param entraTenantId string = tenant().tenantId

@description('Entra object ID allowed to claim the single first-owner bootstrap.')
@minLength(36)
@maxLength(36)
param firstOwnerObjectId string

@description('PostgreSQL Flexible Server compute SKU.')
param postgresSku string = 'Standard_B1ms'

@description('PostgreSQL Flexible Server storage in GB.')
@minValue(32)
@maxValue(16384)
param postgresStorageGb int = 32

@description('Object ID of the Entra principal that administers PostgreSQL. Defaults to the deployer supplied by up.sh.')
param postgresEntraAdminObjectId string

@description('User principal name or display name of the Entra PostgreSQL administrator.')
param postgresEntraAdminPrincipalName string

@description('Directory object kind of the Entra PostgreSQL administrator.')
@allowed([
  'User'
  'Group'
  'ServicePrincipal'
])
param postgresEntraAdminPrincipalType string = 'User'

@description('Storage account redundancy.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_GZRS'
  'Standard_RAGRS'
  'Standard_RAGZRS'
])
param storageRedundancy string = 'Standard_LRS'

@description('Lowest API replica count.')
@minValue(0)
@maxValue(30)
param apiMinReplicas int = 1

@description('Highest API replica count.')
@minValue(1)
@maxValue(30)
param apiMaxReplicas int = 2

@description('Lowest worker replica count.')
@minValue(0)
@maxValue(30)
param workerMinReplicas int = 1

@description('Highest worker replica count.')
@minValue(1)
@maxValue(30)
param workerMaxReplicas int = 1

@description('Monthly cost budget for the resource group, in the billing currency.')
@minValue(1)
param budgetAmount int = 25

@description('Email addresses that receive budget alerts.')
param budgetContactEmails array = []

@description('First day of the month the budget starts tracking. Evaluated once as a parameter default so redeployment stays idempotent.')
param budgetStartDate string = utcNow('yyyy-MM-01')

@description('Optional custom hostname for the API ingress. Never required for an evaluation deployment.')
param customDomainName string = ''

@description('Resource ID of the managed environment certificate bound to the custom hostname.')
param customDomainCertificateId string = ''

@description('Whether the Entra application registration is created from deploy/azure/infra/entra. Set false when the deployer has no Microsoft Graph rights.')
param deployAppRegistration bool = true

@description('Client ID of an already registered Entra application. Required when deployAppRegistration is false, and supplied by up.sh on the pass that turns Entra sign-in on.')
param existingApplicationClientId string = ''

@description('Keep PostgreSQL, Storage, and Key Vault behind CanNotDelete locks so teardown never removes evaluation data.')
param retainData bool = true

// ---------------------------------------------------------------------------
// Naming and derived values
// ---------------------------------------------------------------------------

var environmentSlug = toLower(environmentName)
var resourceToken = toLower(uniqueString(subscription().id, environmentSlug, location))

var apiAppName = 'ca-api-${environmentSlug}'
var workerAppName = 'ca-worker-${environmentSlug}'
var migrationJobName = 'cj-migrate-${environmentSlug}'
var managedEnvironmentName = 'cae-vistara-${environmentSlug}'
var logAnalyticsName = 'log-vistara-${environmentSlug}'
var apiIdentityName = 'id-vistara-api-${environmentSlug}'
var workerIdentityName = 'id-vistara-worker-${environmentSlug}'
var migrateIdentityName = 'id-vistara-migrate-${environmentSlug}'
var storageAccountName = 'stvistara${resourceToken}'
var keyVaultName = 'kv-vistara-${take(resourceToken, 11)}'
var postgresServerName = 'psql-vistara-${resourceToken}'
var budgetName = 'budget-vistara-${environmentSlug}'

var mediaContainerName = 'media'
var dataProtectionContainerName = 'dataprotection'
var postgresDatabaseName = 'vistara'
var postgresApiRole = 'vistara_api_runtime'
var postgresWorkerRole = 'vistara_worker_runtime'
var postgresMigratorRole = 'vistara_migrator'
var registryPasswordSecretName = 'registry-password'

// substring fails the deployment when '@sha256:' is absent, which is the only
// assertion primitive available without the experimental assertions feature.
var apiImageDigest = substring(apiImage, indexOf(apiImage, '@sha256:'))
var workerImageDigest = substring(workerImage, indexOf(workerImage, '@sha256:'))
var migrationImageDigest = substring(migrationImage, indexOf(migrationImage, '@sha256:'))

var applicationClientId = trim(existingApplicationClientId)

// main.parameters.json always supplies entraTenantId so up.sh --tenant-id can
// reach the template; an unset azd value arrives as an empty string.
var resolvedTenantId = empty(trim(entraTenantId)) ? tenant().tenantId : trim(entraTenantId)
var entraAuthority = '${environment().authentication.loginEndpoint}${resolvedTenantId}/v2.0'
var entraMetadataAddress = '${entraAuthority}/.well-known/openid-configuration'

var commonTags = {
  'azd-env-name': environmentName
  'vistara-workload': 'hosted-evaluation'
}

var dataTags = union(commonTags, {
  'azd-retain': string(retainData)
})

// ---------------------------------------------------------------------------
// Resource group
// ---------------------------------------------------------------------------

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: union(commonTags, {
    // down.sh reads this to decide whether teardown also removes an Entra
    // application registration created for this environment.
    'vistara-app-registration': deployAppRegistration ? 'template-managed' : 'operator-supplied'
  })
}

// ---------------------------------------------------------------------------
// Platform modules
// ---------------------------------------------------------------------------

module identity 'modules/identity.bicep' = {
  scope: resourceGroup
  name: 'vistara-identity'
  params: {
    location: location
    tags: commonTags
    apiIdentityName: apiIdentityName
    workerIdentityName: workerIdentityName
    migrateIdentityName: migrateIdentityName
  }
}

module monitoring 'modules/monitoring.bicep' = {
  scope: resourceGroup
  name: 'vistara-monitoring'
  params: {
    location: location
    tags: commonTags
    name: logAnalyticsName
  }
}

module containerAppsEnvironment 'modules/environment.bicep' = {
  scope: resourceGroup
  name: 'vistara-environment'
  params: {
    location: location
    tags: commonTags
    name: managedEnvironmentName
    logAnalyticsWorkspaceName: monitoring.outputs.name
  }
}

module storage 'modules/storage.bicep' = {
  scope: resourceGroup
  name: 'vistara-storage'
  params: {
    location: location
    tags: dataTags
    name: storageAccountName
    storageRedundancy: storageRedundancy
    mediaContainerName: mediaContainerName
    dataProtectionContainerName: dataProtectionContainerName
    retainData: retainData
  }
}

module keyVault 'modules/keyvault.bicep' = {
  scope: resourceGroup
  name: 'vistara-keyvault'
  params: {
    location: location
    tags: dataTags
    name: keyVaultName
    tenantId: resolvedTenantId
    retainData: retainData
  }
}

module postgres 'modules/postgres.bicep' = {
  scope: resourceGroup
  name: 'vistara-postgres'
  params: {
    location: location
    tags: dataTags
    name: postgresServerName
    sku: postgresSku
    storageGb: postgresStorageGb
    databaseName: postgresDatabaseName
    entraTenantId: resolvedTenantId
    entraAdminObjectId: postgresEntraAdminObjectId
    entraAdminPrincipalName: postgresEntraAdminPrincipalName
    entraAdminPrincipalType: postgresEntraAdminPrincipalType
    retainData: retainData
  }
}

module rbac 'modules/rbac.bicep' = {
  scope: resourceGroup
  name: 'vistara-rbac'
  params: {
    storageAccountName: storage.outputs.name
    mediaContainerName: storage.outputs.mediaContainerName
    dataProtectionContainerName: storage.outputs.dataProtectionContainerName
    keyVaultName: keyVault.outputs.name
    apiPrincipalId: identity.outputs.apiPrincipalId
    workerPrincipalId: identity.outputs.workerPrincipalId
  }
}

module budget 'modules/budget.bicep' = {
  scope: resourceGroup
  name: 'vistara-budget'
  params: {
    name: budgetName
    amount: budgetAmount
    startDate: budgetStartDate
    contactEmails: budgetContactEmails
  }
}

// ---------------------------------------------------------------------------
// Application configuration
// ---------------------------------------------------------------------------

var apiDefaultFqdn = '${apiAppName}.${containerAppsEnvironment.outputs.defaultDomain}'
var apiHost = empty(customDomainName) ? apiDefaultFqdn : customDomainName
var apiUri = 'https://${apiHost}'

var postgresFqdn = postgres.outputs.fullyQualifiedDomainName

// No password appears in any connection string: Npgsql supplies an Entra
// access token through the periodic password provider.
func postgresConnectionString(host string, role string, database string) string =>
  'Host=${host};Port=5432;Database=${database};Username=${role};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Include Error Detail=false'

var apiConnectionString = postgresConnectionString(postgresFqdn, postgresApiRole, postgresDatabaseName)
var workerConnectionString = postgresConnectionString(postgresFqdn, postgresWorkerRole, postgresDatabaseName)
var migrationConnectionString = postgresConnectionString(postgresFqdn, postgresMigratorRole, postgresDatabaseName)

var allowedHosts = empty(customDomainName)
  ? [
      {
        name: 'Security__Hosts__AllowedHosts__0'
        value: apiDefaultFqdn
      }
    ]
  : [
      {
        name: 'Security__Hosts__AllowedHosts__0'
        value: apiDefaultFqdn
      }
      {
        name: 'Security__Hosts__AllowedHosts__1'
        value: customDomainName
      }
    ]

var mediaEnvironmentVariables = [
  {
    name: 'Media__Storage__Provider'
    value: 'Azure'
  }
  {
    name: 'Media__Storage__Azure__AccountName'
    value: storage.outputs.name
  }
  {
    name: 'Media__Storage__Azure__ContainerName'
    value: mediaContainerName
  }
  {
    name: 'Media__Storage__Azure__CredentialMode'
    value: 'ManagedIdentity'
  }
  {
    name: 'Media__Imaging__Provider'
    value: 'NetVips'
  }
]

// PlatformOptionsValidator still requires at least one JWT issuer for the
// machine API surface, so the Entra tenant issuer stays configured whether or
// not interactive Entra sign-in is turned on yet.
var jwtEnvironmentVariables = [
  {
    name: 'Platform__Authentication__Jwt__Issuers__0__ProfileId'
    value: 'entra'
  }
  {
    name: 'Platform__Authentication__Jwt__Issuers__0__Issuer'
    value: entraAuthority
  }
  {
    name: 'Platform__Authentication__Jwt__Issuers__0__Audience'
    value: empty(applicationClientId) ? 'vistara-api' : applicationClientId
  }
  {
    name: 'Platform__Authentication__Jwt__Issuers__0__MetadataAddress'
    value: entraMetadataAddress
  }
  {
    name: 'Platform__Authentication__Jwt__Issuers__0__AllowedAlgorithms__0'
    value: 'RS256'
  }
]

// Interactive sign-in and first-owner bootstrap only appear once a client ID
// exists. Until then the deployment is healthy and Entra sign-in is simply
// absent from /api/v1/setup rather than half-configured.
var oidcEnvironmentVariables = empty(applicationClientId)
  ? []
  : [
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ProviderId'
        value: 'entra'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__DisplayName'
        value: 'Microsoft Entra ID'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__MetadataAddress'
        value: entraMetadataAddress
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ExpectedIssuer'
        value: entraAuthority
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ClientId'
        value: applicationClientId
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__AllowedTenantIds__0'
        value: resolvedTenantId
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__RedirectUri'
        value: '${apiUri}/api/v1/auth/oidc/entra/callback'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__Scopes__0'
        value: 'openid'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__Scopes__1'
        value: 'profile'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__Scopes__2'
        value: 'email'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__Prompt'
        value: 'select_account'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__LoginRequestLifetime'
        value: '00:10:00'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ClockSkew'
        value: '00:02:00'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__AllowHttpMetadata'
        value: 'false'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ClientCredential__Kind'
        value: 'FederatedManagedIdentity'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ClientCredential__ManagedIdentityClientId'
        value: identity.outputs.apiClientId
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ClientCredential__TokenExchangeAudience'
        value: 'api://AzureADTokenExchange'
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__ProviderId'
        value: 'entra'
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__TenantId'
        value: resolvedTenantId
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__AllowedObjectIds__0'
        value: firstOwnerObjectId
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__TenantSlug'
        value: 'default'
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__TenantName'
        value: 'Vistara'
      }
    ]

var apiEnvironmentVariables = concat(
  [
    {
      name: 'ASPNETCORE_ENVIRONMENT'
      value: 'Production'
    }
    {
      name: 'Persistence__Provider'
      value: 'PostgreSql'
    }
    {
      name: 'ConnectionStrings__Vistara'
      value: apiConnectionString
    }
    {
      name: 'Persistence__Azure__EntraTokenEnabled'
      value: 'true'
    }
    {
      name: 'Persistence__Azure__ManagedIdentityClientId'
      value: identity.outputs.apiClientId
    }
    {
      name: 'Persistence__Azure__TokenRefreshInterval'
      value: '00:55:00'
    }
    {
      name: 'Persistence__Azure__TokenRetryInterval'
      value: '00:00:05'
    }
    {
      name: 'Security__Transport__RedirectHttpToHttps'
      value: 'true'
    }
    {
      name: 'Security__Proxy__ForwardLimit'
      value: '1'
    }
    {
      name: 'Security__DataProtection__ApplicationDiscriminator'
      value: 'vistara-${environmentSlug}'
    }
    {
      name: 'Security__DataProtection__BlobUri'
      value: storage.outputs.dataProtectionBlobUri
    }
    {
      name: 'Security__DataProtection__KeyVaultKeyId'
      value: keyVault.outputs.dataProtectionKeyId
    }
    {
      name: 'Security__DataProtection__ManagedIdentityClientId'
      value: identity.outputs.apiClientId
    }
    {
      name: 'Media__Storage__Azure__ManagedIdentityClientId'
      value: identity.outputs.apiClientId
    }
    {
      name: 'Telemetry__ServiceName'
      value: 'vistara-api'
    }
  ],
  allowedHosts,
  mediaEnvironmentVariables,
  jwtEnvironmentVariables,
  oidcEnvironmentVariables
)

var workerEnvironmentVariables = concat(
  [
    {
      name: 'DOTNET_ENVIRONMENT'
      value: 'Production'
    }
    {
      name: 'Persistence__Provider'
      value: 'PostgreSql'
    }
    {
      name: 'ConnectionStrings__Vistara'
      value: workerConnectionString
    }
    {
      name: 'Persistence__Azure__EntraTokenEnabled'
      value: 'true'
    }
    {
      name: 'Persistence__Azure__ManagedIdentityClientId'
      value: identity.outputs.workerClientId
    }
    {
      name: 'Persistence__Azure__TokenRefreshInterval'
      value: '00:55:00'
    }
    {
      name: 'Persistence__Azure__TokenRetryInterval'
      value: '00:00:05'
    }
    {
      name: 'Media__Storage__Azure__ManagedIdentityClientId'
      value: identity.outputs.workerClientId
    }
    {
      name: 'Worker__InstanceId'
      value: workerAppName
    }
    {
      name: 'Worker__Jobs__MaximumConcurrency'
      value: '1'
    }
    {
      name: 'Worker__ImagingLimits__MaximumConcurrentTransforms'
      value: '1'
    }
    {
      name: 'Telemetry__ServiceName'
      value: 'vistara-worker'
    }
  ],
  mediaEnvironmentVariables
)

var migrationEnvironmentVariables = [
  {
    name: 'MIGRATION_PROVIDER'
    value: 'PostgreSql'
  }
  {
    name: 'ConnectionStrings__Vistara'
    value: migrationConnectionString
  }
  {
    name: 'Persistence__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Persistence__Azure__EntraTokenEnabled'
    value: 'true'
  }
  {
    name: 'Persistence__Azure__ManagedIdentityClientId'
    value: identity.outputs.migrateClientId
  }
  {
    name: 'Persistence__Azure__TokenRefreshInterval'
    value: '00:55:00'
  }
  {
    name: 'Persistence__Azure__TokenRetryInterval'
    value: '00:00:05'
  }
]

// A private registry password only ever travels as a Key Vault reference that
// the operator created before provisioning.
var privateRegistry = !empty(registryPasswordSecretUri)

var apiRegistrySecrets = privateRegistry
  ? [
      {
        name: registryPasswordSecretName
        keyVaultUrl: registryPasswordSecretUri
        identity: identity.outputs.apiResourceId
      }
    ]
  : []

var workerRegistrySecrets = privateRegistry
  ? [
      {
        name: registryPasswordSecretName
        keyVaultUrl: registryPasswordSecretUri
        identity: identity.outputs.workerResourceId
      }
    ]
  : []

var migrationRegistrySecrets = privateRegistry
  ? [
      {
        name: registryPasswordSecretName
        keyVaultUrl: registryPasswordSecretUri
        identity: identity.outputs.migrateResourceId
      }
    ]
  : []

var registries = privateRegistry
  ? [
      {
        server: registryServer
        username: registryUsername
        passwordSecretRef: registryPasswordSecretName
      }
    ]
  : []

// ---------------------------------------------------------------------------
// Compute modules
// ---------------------------------------------------------------------------

module migrationJob 'modules/migrate-job.bicep' = {
  scope: resourceGroup
  name: 'vistara-migrate-job'
  params: {
    location: location
    tags: union(commonTags, {
      'vistara-image-digest': migrationImageDigest
    })
    name: migrationJobName
    managedEnvironmentResourceId: containerAppsEnvironment.outputs.resourceId
    userAssignedIdentityResourceId: identity.outputs.migrateResourceId
    image: migrationImage
    secrets: migrationRegistrySecrets
    registries: registries
    environmentVariables: migrationEnvironmentVariables
  }
  dependsOn: [
    rbac
  ]
}

module api 'modules/api.bicep' = {
  scope: resourceGroup
  name: 'vistara-api'
  params: {
    location: location
    tags: union(commonTags, {
      'azd-service-name': 'api'
      'vistara-image-digest': apiImageDigest
    })
    name: apiAppName
    managedEnvironmentResourceId: containerAppsEnvironment.outputs.resourceId
    userAssignedIdentityResourceId: identity.outputs.apiResourceId
    image: apiImage
    secrets: apiRegistrySecrets
    registries: registries
    environmentVariables: apiEnvironmentVariables
    minReplicas: apiMinReplicas
    maxReplicas: apiMaxReplicas
    customDomainName: customDomainName
    customDomainCertificateId: customDomainCertificateId
  }
  dependsOn: [
    rbac
  ]
}

module worker 'modules/worker.bicep' = {
  scope: resourceGroup
  name: 'vistara-worker'
  params: {
    location: location
    tags: union(commonTags, {
      'azd-service-name': 'worker'
      'vistara-image-digest': workerImageDigest
    })
    name: workerAppName
    managedEnvironmentResourceId: containerAppsEnvironment.outputs.resourceId
    userAssignedIdentityResourceId: identity.outputs.workerResourceId
    image: workerImage
    secrets: workerRegistrySecrets
    registries: registries
    environmentVariables: workerEnvironmentVariables
    minReplicas: workerMinReplicas
    maxReplicas: workerMaxReplicas
  }
  dependsOn: [
    rbac
  ]
}

// ---------------------------------------------------------------------------
// Outputs. Every value below is public configuration: no secret, key, token,
// connection password, or shared access signature is ever emitted.
// ---------------------------------------------------------------------------

output AZURE_TENANT_ID string = resolvedTenantId
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_LOCATION string = location

output SERVICE_API_URI string = apiUri
output ENTRA_APPLICATION_CLIENT_ID string = applicationClientId

output API_IDENTITY_CLIENT_ID string = identity.outputs.apiClientId
output API_IDENTITY_PRINCIPAL_ID string = identity.outputs.apiPrincipalId
output WORKER_IDENTITY_CLIENT_ID string = identity.outputs.workerClientId
output WORKER_IDENTITY_PRINCIPAL_ID string = identity.outputs.workerPrincipalId
output MIGRATE_IDENTITY_CLIENT_ID string = identity.outputs.migrateClientId
output MIGRATE_IDENTITY_PRINCIPAL_ID string = identity.outputs.migratePrincipalId

output POSTGRES_HOST string = postgresFqdn
output POSTGRES_DATABASE string = postgres.outputs.databaseName
output POSTGRES_API_ROLE string = postgresApiRole
output POSTGRES_WORKER_ROLE string = postgresWorkerRole
output POSTGRES_MIGRATOR_ROLE string = postgresMigratorRole

output AZURE_STORAGE_ACCOUNT_NAME string = storage.outputs.name
output AZURE_STORAGE_BLOB_ENDPOINT string = storage.outputs.blobEndpoint
output MEDIA_CONTAINER_NAME string = storage.outputs.mediaContainerName
output DATAPROTECTION_BLOB_URI string = storage.outputs.dataProtectionBlobUri
output DATAPROTECTION_KEY_ID string = keyVault.outputs.dataProtectionKeyId

output AZURE_KEY_VAULT_ENDPOINT string = keyVault.outputs.vaultUri

output MIGRATION_JOB_NAME string = migrationJob.outputs.name
output API_CONTAINER_APP_NAME string = api.outputs.name
output WORKER_CONTAINER_APP_NAME string = worker.outputs.name
