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

@description('First day of the month the budget tracks, as yyyy-MM-01. Never defaulted from the clock: a start date that moves with the deployment month makes every provision a different budget, so up.sh writes VISTARA_BUDGET_START_DATE into the azd environment once and every later provision reuses it.')
@minLength(10)
@maxLength(10)
param budgetStartDate string

@description('Optional custom hostname for the API ingress. Never required for an evaluation deployment.')
param customDomainName string = ''

@description('Resource ID of the managed environment certificate bound to the custom hostname.')
param customDomainCertificateId string = ''

@description('Whether the Entra application registration is created from deploy/azure/infra/entra. Set false when the deployer has no Microsoft Graph rights.')
param deployAppRegistration bool = true

@description('Client ID of an already registered Entra application. Required when deployAppRegistration is false, and supplied by up.sh on the pass that turns Entra sign-in on.')
param existingApplicationClientId string = ''

@description('Activate the API and worker container apps. The first provision leaves this false so data, identity, Key Vault, the environment, and the migration job exist before any application replica starts; up.sh sets it true once the pepper, the Entra application, and the database roles are in place.')
param deployApplications bool = false

@description('Key Vault secret URI holding the API key pepper. Written by up.sh between the two provisions, never a value, and required once deployApplications is true.')
param apiKeyPepperSecretUri string = ''

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

// dateTimeAdd renders the first day of whatever month the supplied date falls
// in, so indexOf returns 0 only when the operator supplied that exact day and
// -1 otherwise, and substring then fails the deployment. Together with the
// ten-character length constraint on the parameter this admits exactly
// yyyy-MM-01 and rejects a mid-month date, an unpadded date, a timestamp, and
// anything that is not a date at all. The deployment fails rather than silently
// re-basing a budget that Cost Management has already been accruing against.
var budgetStartDateMonthStart = dateTimeAdd(budgetStartDate, 'P0D', 'yyyy-MM-01')
var budgetStartDateChecked = substring(
  budgetStartDate,
  indexOf(budgetStartDate, budgetStartDateMonthStart)
)

var mediaContainerName = 'media'
var dataProtectionContainerName = 'dataprotection'
var dataProtectionBlobName = 'keys.xml'
var postgresDatabaseName = 'vistara'
var postgresPort = 5432
var postgresTokenScope = 'https://ossrdbms-aad${environment().suffixes.sqlServerHostname}/.default'
var postgresApiRole = 'vistara_api_runtime'
var postgresWorkerRole = 'vistara_worker_runtime'
var postgresMigratorRole = 'vistara_migrator'
var registryPasswordSecretName = 'registry-password'
var apiKeyPepperSecretName = 'api-key-pepper'
var apiKeyPepperVersion = 'v1'
var apiKeyPepperConfigurationKey = 'Platform:Authentication:ApiKeys:Peppers:${apiKeyPepperVersion}'

// The frozen OidcRoutes reply paths. The provider validator compares them byte
// for byte against the Entra registration, so they are named here rather than
// spelled out at each use.
var oidcCallbackPath = '/api/v1/auth/oidc/entra/callback'
var oidcSignedOutPath = '/api/v1/auth/oidc/entra/signed-out'

// The hosted rate profile, which is PlatformRateLimitHostedProfile expressed as
// container environment variables. Both ceilings - the persisted bucket counter
// and the in-process framework limiter - count the connection peer after
// forwarded-header processing, and behind the Container Apps ingress that peer
// is always the proxy, so every request in the deployment shares one bucket.
// PartitionMode says so out loud: the application refuses a bucket raised above
// its shipped rate unless the deployment has declared whose requests the bucket
// counts, which stops hosted-scale ceilings from being handed to every client
// on a deployment that really can see them.
//
// Events stays an order of magnitude below the other buckets because a stream
// holds a connection, and the sensitive surfaces keep their own guards.
var hostedRateLimitPartitionMode = 'SharedIngress'
var hostedRateLimitWindow = '00:01:00'
var hostedRateLimitApi = 6000
var hostedRateLimitEvents = 600
var hostedRateLimitDelivery = 6000
var hostedRateLimitMedia = 6000
var sharedIngressRequestsPerWindow = 6000
var sharedIngressRateLimitWindow = '00:01:00'

// Ordered exactly as PlatformRateLimitHostedProfile.EnvironmentVariables.
var hostedRateLimitEnvironmentVariables = [
  {
    name: 'Platform__RateLimits__PartitionMode'
    value: hostedRateLimitPartitionMode
  }
  {
    name: 'Platform__RateLimits__Window'
    value: hostedRateLimitWindow
  }
  {
    name: 'Platform__RateLimits__Api'
    value: string(hostedRateLimitApi)
  }
  {
    name: 'Platform__RateLimits__Events'
    value: string(hostedRateLimitEvents)
  }
  {
    name: 'Platform__RateLimits__Delivery'
    value: string(hostedRateLimitDelivery)
  }
  {
    name: 'Platform__RateLimits__Media'
    value: string(hostedRateLimitMedia)
  }
  {
    name: 'Security__Limits__RequestsPerWindow'
    value: string(sharedIngressRequestsPerWindow)
  }
  {
    name: 'Security__Limits__RateLimitWindow'
    value: sharedIngressRateLimitWindow
  }
]

// substring fails the deployment when '@sha256:' is absent, which is the only
// assertion primitive available without the experimental assertions feature.
var apiImageDigest = substring(apiImage, indexOf(apiImage, '@sha256:'))
var workerImageDigest = substring(workerImage, indexOf(workerImage, '@sha256:'))
var migrationImageDigest = substring(migrationImage, indexOf(migrationImage, '@sha256:'))

var applicationClientId = trim(existingApplicationClientId)

// A private registry password only ever travels as a Key Vault reference that
// the operator created before provisioning.
var privateRegistry = !empty(registryPasswordSecretUri)

// substring() over a missing '/secrets/' fails the deployment here rather than
// letting Container Apps reject an unresolvable Key Vault reference later.
var apiKeyPepperSecretUriChecked = !deployApplications
  ? ''
  : (contains(apiKeyPepperSecretUri, '/secrets/')
      ? apiKeyPepperSecretUri
      : substring(apiKeyPepperSecretUri, indexOf(apiKeyPepperSecretUri, '/secrets/')))

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
    dataProtectionBlobName: dataProtectionBlobName
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
    migratePrincipalId: identity.outputs.migratePrincipalId
    grantMigrateKeyVaultSecretsUser: privateRegistry
  }
}

module budget 'modules/budget.bicep' = {
  scope: resourceGroup
  name: 'vistara-budget'
  params: {
    name: budgetName
    amount: budgetAmount
    startDate: budgetStartDateChecked
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
func postgresConnectionString(host string, port int, role string, database string) string =>
  'Host=${host};Port=${port};Database=${database};Username=${role};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Include Error Detail=false'

// Only the API and worker bind a connection string. The migration job builds
// its own from discrete variables, so this value is never handed to the job.
var apiConnectionString = postgresConnectionString(postgresFqdn, postgresPort, postgresApiRole, postgresDatabaseName)
var workerConnectionString = postgresConnectionString(postgresFqdn, postgresPort, postgresWorkerRole, postgresDatabaseName)

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
  // Required by the Azure media provider so the blob endpoint is asserted
  // against the account rather than inferred from an ambient suffix.
  {
    name: 'Media__Storage__Azure__ServiceUri'
    value: storage.outputs.blobEndpoint
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
//
// Every name below is a property of PlatformOidcOptions, PlatformOidcProviderOptions,
// PlatformBootstrapOptions, or PlatformFirstOwnerOptions. The reply URLs are the
// frozen OidcRoutes paths, which the provider validator compares byte for byte
// against the registration, and the application base URL must contain the reply
// URL, so it carries the trailing slash the validator requires.
var oidcEnvironmentVariables = empty(applicationClientId)
  ? []
  : [
      {
        name: 'Platform__Authentication__Oidc__Enabled'
        value: 'true'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ProviderId'
        value: 'entra'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__DisplayName'
        value: 'Microsoft Entra ID'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__TenantId'
        value: resolvedTenantId
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ClientId'
        value: applicationClientId
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__Authority'
        value: entraAuthority
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ApplicationBaseUri'
        value: '${apiUri}/'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__RedirectUri'
        value: '${apiUri}${oidcCallbackPath}'
      }
      {
        name: 'Platform__Authentication__Oidc__Providers__0__PostLogoutRedirectUri'
        value: '${apiUri}${oidcSignedOutPath}'
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
        name: 'Platform__Authentication__Oidc__Providers__0__AllowedSigningAlgorithms__0'
        value: 'RS256'
      }
      // Loopback HTTP is only ever allowed for an integration fixture.
      {
        name: 'Platform__Authentication__Oidc__Providers__0__RequireHttps'
        value: 'true'
      }
      // The secretless path: the API identity mints the federated client
      // assertion, so no client secret exists to configure.
      {
        name: 'Platform__Authentication__Oidc__Providers__0__ManagedIdentityClientId'
        value: identity.outputs.apiClientId
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__Enabled'
        value: 'true'
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__ProviderId'
        value: 'entra'
      }
      {
        name: 'Platform__Bootstrap__FirstOwner__DirectoryTenantId'
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
      name: 'Persistence__Azure__TokenScope'
      value: postgresTokenScope
    }
    // Container Apps ingress already refuses plain HTTP (allowInsecure is
    // false), and the forwarded-header middleware trusts no proxy address by
    // default, so an in-process redirect would loop against the edge instead of
    // upgrading anything.
    {
      name: 'Security__Transport__RedirectHttpToHttps'
      value: 'false'
    }
    // Container Apps terminates TLS at a shared ingress proxy. Microsoft
    // publishes no address range for the internal hop that reaches the replica,
    // and managedEnvironments.staticIp is the environment's own ingress/egress
    // address rather than the peer the container observes, so nothing here can
    // populate Security__Proxy__KnownProxies or Security__Proxy__KnownNetworks
    // with a reviewed CIDR. The API therefore trusts no forwarded address and
    // discards X-Forwarded-For. Security__Proxy__ForwardLimit is deliberately
    // not emitted: without a trust list it configures nothing, and shipping it
    // would suggest the deployment reads a client address that it does not.
    // What follows from that is the hosted rate profile appended below.
    {
      name: 'Security__DataProtection__Enabled'
      value: 'true'
    }
    {
      name: 'Security__DataProtection__ApplicationDiscriminator'
      value: 'vistara-${environmentSlug}'
    }
    {
      name: 'Security__DataProtection__BlobServiceUri'
      value: storage.outputs.blobEndpoint
    }
    {
      name: 'Security__DataProtection__BlobContainerName'
      value: dataProtectionContainerName
    }
    {
      name: 'Security__DataProtection__KeyBlobName'
      value: dataProtectionBlobName
    }
    {
      name: 'Security__DataProtection__KeyVaultKeyIdentifier'
      value: keyVault.outputs.dataProtectionKeyId
    }
    {
      name: 'Security__DataProtection__ManagedIdentityClientId'
      value: identity.outputs.apiClientId
    }
    {
      name: 'Security__RequiredSecretKeys__0'
      value: apiKeyPepperConfigurationKey
    }
    {
      name: 'Platform__Authentication__ApiKeys__CurrentPepperVersion'
      value: apiKeyPepperVersion
    }
    {
      name: 'Platform__Authentication__ApiKeys__Peppers__${apiKeyPepperVersion}'
      secretRef: apiKeyPepperSecretName
    }
    {
      name: 'Media__Storage__Azure__ManagedIdentityClientId'
      value: identity.outputs.apiClientId
    }
    // Azure SDK clients that are handed no explicit client id still resolve a
    // user-assigned identity from this variable, so no ambient identity on the
    // replica can be picked up instead.
    {
      name: 'AZURE_CLIENT_ID'
      value: identity.outputs.apiClientId
    }
    {
      name: 'Telemetry__ServiceName'
      value: 'vistara-api'
    }
  ],
  allowedHosts,
  hostedRateLimitEnvironmentVariables,
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
      name: 'Persistence__Azure__TokenScope'
      value: postgresTokenScope
    }
    {
      name: 'Media__Storage__Azure__ManagedIdentityClientId'
      value: identity.outputs.workerClientId
    }
    {
      name: 'AZURE_CLIENT_ID'
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

// The migration entrypoint reads its token straight from the Container Apps
// identity endpoint and builds the connection string itself from discrete,
// individually validated values. Npgsql accepts keyword aliases and repeated
// keywords, so a connection string handed to the job could smuggle a password
// past a string-inspecting guard; deploy/containers/migration-entrypoint.sh
// therefore ignores ConnectionStrings__Vistara and Persistence__ConnectionString
// entirely, and this template never emits either one for the job.
var migrationEnvironmentVariables = [
  {
    name: 'MIGRATION_PROVIDER'
    value: 'PostgreSql'
  }
  {
    name: 'MIGRATION_MANAGED_IDENTITY_CLIENT_ID'
    value: identity.outputs.migrateClientId
  }
  {
    name: 'MIGRATION_POSTGRES_HOST'
    value: postgresFqdn
  }
  {
    name: 'MIGRATION_POSTGRES_PORT'
    value: string(postgresPort)
  }
  {
    name: 'MIGRATION_POSTGRES_DATABASE'
    value: postgres.outputs.databaseName
  }
  {
    name: 'MIGRATION_POSTGRES_USERNAME'
    value: postgresMigratorRole
  }
  {
    name: 'MIGRATION_ENTRA_TOKEN_SCOPE'
    value: postgresTokenScope
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.outputs.migrateClientId
  }
]

var apiRegistrySecrets = privateRegistry
  ? [
      {
        name: registryPasswordSecretName
        keyVaultUrl: registryPasswordSecretUri
        identity: identity.outputs.apiResourceId
      }
    ]
  : []

// The pepper is generated and written to Key Vault by up.sh between the two
// provisions, so the reference only exists on the activation pass.
var apiKeyPepperSecrets = deployApplications
  ? [
      {
        name: apiKeyPepperSecretName
        keyVaultUrl: apiKeyPepperSecretUriChecked
        identity: identity.outputs.apiResourceId
      }
    ]
  : []

var apiSecrets = concat(apiRegistrySecrets, apiKeyPepperSecrets)

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

// Deployed on both passes: HB-12 starts this job and waits for it to succeed
// before the activation pass turns the API and worker on. A manual trigger
// never starts a replica by itself, so an idle job cannot crash-loop.
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

module api 'modules/api.bicep' = if (deployApplications) {
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
    secrets: apiSecrets
    registries: registries
    environmentVariables: apiEnvironmentVariables
    minReplicas: apiMinReplicas
    maxReplicas: apiMaxReplicas
    // Probes must present a host the API allows, and apiHost is exactly the
    // host Security__Hosts__AllowedHosts carries for this deployment.
    ingressHost: apiHost
    customDomainName: customDomainName
    customDomainCertificateId: customDomainCertificateId
  }
  dependsOn: [
    rbac
  ]
}

module worker 'modules/worker.bicep' = if (deployApplications) {
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
//
// No output describes the ingress proxy's internal source addresses, and none
// is intended to become a forwarded-header trust list. The caller's IP address
// is not observable in this topology: the replica only ever sees the Container
// Apps proxy, and X-Forwarded-For is untrusted input until a reviewed edge with
// published proxy CIDRs terminates traffic. Anything that needs a per-client
// identity must use an authenticated principal, not a synthesised address.
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
output API_CONTAINER_APP_NAME string = deployApplications ? api!.outputs.name : ''
output WORKER_CONTAINER_APP_NAME string = deployApplications ? worker!.outputs.name : ''
