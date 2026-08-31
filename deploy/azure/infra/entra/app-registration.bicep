// Entra application registration for the Vistara hosted API sign-in path.
//
// Deployed through the Microsoft Graph Bicep extension pinned in
// `deploy/azure/bicepconfig.json`. The Graph alternate key `uniqueName` is
// scope-discriminated below, so repeated deployments of the same environment
// into the same tenant, subscription, and resource group upsert one
// application, while a different tenant, subscription, or resource group gets
// its own. This module is idempotent by construction and never emits a
// credential.
//
// Scope: the caller (`infra/main.bicep`, owned by HB-08) is subscription
// scoped, so it must invoke this module with `scope: resourceGroup(...)`.
//
// Redirect-list ownership: `web.redirectUris` is declarative and replaces the
// stored list on every deployment. That is safe only because the alternate key
// is scope-discriminated: this module can only ever address the application it
// owns, so it cannot overwrite reply URLs belonging to another environment,
// subscription, or tenant. Every entry in the list is derived from `apiFqdn` —
// nothing external is merged in, and nothing external may be.
//
// HB-12 fallback (`--skip-app-registration`): a deployer without directory
// rights (`Application.ReadWrite.All`, or `Application.ReadWrite.OwnedBy` for a
// service principal) cannot deploy this module at all — the Graph extension
// fails the whole deployment. `main.bicep` therefore guards the module call
// with `deployAppRegistration` and accepts `existingApplicationClientId`
// instead. When that path is taken, the operator must reproduce this exact
// shape by hand and `azd env set ENTRA_APPLICATION_CLIENT_ID`:
//
//   az ad app create --display-name "<displayName>" \
//     --sign-in-audience AzureADMyOrg \
//     --web-redirect-uris "https://<apiFqdn>/api/v1/auth/oidc/entra/callback" \
//                         "https://<apiFqdn>/api/v1/auth/oidc/entra/signed-out" \
//     --enable-id-token-issuance false --enable-access-token-issuance false
//   az ad app update --id <appObjectId> \
//     --set web.logoutUrl="https://<apiFqdn>/api/v1/auth/oidc/entra/frontchannel-logout"
//   az ad app federated-credential create --id <appObjectId> --parameters '{
//     "name": "api-managed-identity",
//     "issuer": "https://login.microsoftonline.com/<tenantId>/v2.0",
//     "subject": "<apiIdentityPrincipalId>",
//     "audiences": ["api://AzureADTokenExchange"] }'
//   az ad sp create --id <appClientId>
//
// A federated identity credential with a wrong `issuer`, `subject`, or
// `audience` deploys successfully and only fails at token exchange, so
// `hooks/postprovision-verify-fic.sh` (HB-12) byte-compares all three against
// the `federatedCredential*` outputs below.

targetScope = 'resourceGroup'

extension microsoftGraphV1

@description('Environment-derived base for the Graph alternate key, expected shape vistara-<environmentName>. A deterministic tenant, subscription, and resource-group discriminator is appended so the key cannot collide with another deployment in the same directory.')
@minLength(3)
@maxLength(100)
param uniqueName string

@description('Human readable name shown on the Entra consent and sign-in screens.')
@minLength(1)
@maxLength(256)
param displayName string

@description('Public HTTPS host of the API container app, without scheme or trailing slash. The registered reply URLs must match the runtime Platform:Authentication:Oidc values byte for byte.')
@minLength(4)
@maxLength(253)
param apiFqdn string

@description('Object (principal) ID of the API user-assigned managed identity. Becomes the federated identity credential subject, which Entra matches against the managed identity token sub claim.')
@minLength(36)
@maxLength(36)
param apiIdentityPrincipalId string

@description('Entra tenant ID that issues the managed identity token used for the client assertion.')
@minLength(36)
@maxLength(36)
param tenantId string

// Every input is trimmed so a stray newline from `azd env get-value` or a shell
// substitution cannot silently produce a reply URL or federated credential
// subject that no longer matches at runtime. Host names and directory GUIDs are
// lowercased to their canonical Entra form; the display name keeps its casing.
var normalizedUniqueName = toLower(trim(uniqueName))
var normalizedDisplayName = trim(displayName)
var normalizedApiFqdn = toLower(trim(apiFqdn))
var normalizedApiIdentityPrincipalId = toLower(trim(apiIdentityPrincipalId))
var normalizedTenantId = toLower(trim(tenantId))

// The alternate key is directory-wide, but the environment name is not: two
// subscriptions or resource groups in one tenant can both be called `eval`.
// `uniqueString` is a pure hash, so the same scope always yields the same key
// (reruns upsert) and any different scope yields a different one.
var scopeDiscriminator = uniqueString(normalizedTenantId, subscription().subscriptionId, resourceGroup().id)
var effectiveUniqueName = '${normalizedUniqueName}-${scopeDiscriminator}'

// The three hosted routes are frozen: HB-11 serves exactly these paths and
// HB-12 asserts them against the deployed registration.
var callbackRoute = '/api/v1/auth/oidc/entra/callback'
var frontChannelLogoutRoute = '/api/v1/auth/oidc/entra/frontchannel-logout'
var signedOutRoute = '/api/v1/auth/oidc/entra/signed-out'

var apiBaseUri = 'https://${normalizedApiFqdn}'
var callbackUri = '${apiBaseUri}${callbackRoute}'
var frontChannelLogoutUri = '${apiBaseUri}${frontChannelLogoutRoute}'
var signedOutUri = '${apiBaseUri}${signedOutRoute}'

var federatedCredentialName = 'api-managed-identity'
#disable-next-line no-hardcoded-env-urls // The v2.0 issuer is a protocol constant that Entra matches exactly; it cannot come from `environment()`.
var federatedCredentialIssuerValue = 'https://login.microsoftonline.com/${normalizedTenantId}/v2.0'
var federatedCredentialAudienceValue = 'api://AzureADTokenExchange'

// Microsoft Graph first-party app ID and the official delegated permission IDs
// for the only three scopes the sign-in path consumes. The values are held as
// checked-in source of truth in
// `eng/tests/fixtures/azure-graph-registration/microsoft-graph-delegated-permissions.json`.
var microsoftGraphAppId = '00000003-0000-0000-c000-000000000000'
var openIdScopeId = '37f7f235-527c-4136-accd-4a02d197296e'
var profileScopeId = '14dad69e-099b-42c9-810b-d002981feec1'
var emailScopeId = '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0'

resource application 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: effectiveUniqueName
  displayName: normalizedDisplayName
  description: 'Vistara hosted API interactive sign-in. Single tenant, authorization code with PKCE, secretless managed-identity client assertion.'
  signInAudience: 'AzureADMyOrg'
  // No groups claim: the deployment allowlist keys on (tid, oid), and a group
  // overage silently drops the claim, which would fail open.
  groupMembershipClaims: 'None'
  isDeviceOnlyAuthSupported: false
  // Confidential client. The credential is the federated identity credential
  // below; no key or password credential is ever declared or emitted.
  isFallbackPublicClient: false
  keyCredentials: []
  passwordCredentials: []
  optionalClaims: {
    accessToken: []
    idToken: []
    saml2Token: []
  }
  publicClient: {
    redirectUris: []
  }
  spa: {
    redirectUris: []
  }
  web: {
    homePageUrl: apiBaseUri
    // Front-channel sign-out: Entra calls this route on the user's behalf when
    // the session ends elsewhere in the directory.
    logoutUrl: frontChannelLogoutUri
    // Entra requires a post-logout redirect target to be a registered reply
    // URL, so the signed-out landing route is registered alongside the
    // authorization-code callback.
    redirectUris: [
      callbackUri
      signedOutUri
    ]
    // The server-side callback exchanges the code itself, so neither implicit
    // response type is issued to the browser.
    implicitGrantSettings: {
      enableAccessTokenIssuance: false
      enableIdTokenIssuance: false
    }
  }
  requiredResourceAccess: [
    {
      resourceAppId: microsoftGraphAppId
      resourceAccess: [
        {
          id: openIdScopeId
          type: 'Scope'
        }
        {
          id: profileScopeId
          type: 'Scope'
        }
        {
          id: emailScopeId
          type: 'Scope'
        }
      ]
    }
  ]
}

// The Graph extension addresses child resources by the `<alternate key>/<name>`
// path rather than `parent`, so referencing `application.uniqueName` is both the
// required form and what establishes the dependency on the application.
resource apiManagedIdentityCredential 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0' = {
  name: '${application.uniqueName}/${federatedCredentialName}'
  description: 'Trusts the Vistara API user-assigned managed identity to present client assertions for this application.'
  issuer: federatedCredentialIssuerValue
  subject: normalizedApiIdentityPrincipalId
  audiences: [
    federatedCredentialAudienceValue
  ]
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
}

@description('Application (client) ID; the OIDC client_id and the expected id_token aud.')
output applicationClientId string = application.appId

@description('Application object ID; the target of az ad app federated-credential list.')
output applicationObjectId string = application.id

@description('Service principal object ID in this tenant; the target of directory role and consent operations.')
output servicePrincipalObjectId string = servicePrincipal.id

@description('Graph alternate key actually deployed, including the tenant, subscription, and resource-group discriminator.')
output applicationUniqueName string = effectiveUniqueName

@description('Registered authorization-code reply URL; must equal Platform:Authentication:Oidc:Providers:0:RedirectUri byte for byte.')
output redirectUri string = callbackUri

@description('Registered front-channel sign-out URL that Entra calls when a directory session ends.')
output frontChannelLogoutUri string = frontChannelLogoutUri

@description('Registered post-logout reply URL used as post_logout_redirect_uri after RP-initiated sign-out.')
output postLogoutRedirectUri string = signedOutUri

@description('Federated identity credential name, for az ad app federated-credential list.')
output federatedCredentialName string = federatedCredentialName

@description('Expected federated identity credential issuer, for the postprovision byte-compare.')
output federatedCredentialIssuer string = federatedCredentialIssuerValue

@description('Expected federated identity credential subject, for the postprovision byte-compare.')
output federatedCredentialSubject string = normalizedApiIdentityPrincipalId

@description('Expected federated identity credential audience, for the postprovision byte-compare.')
output federatedCredentialAudience string = federatedCredentialAudienceValue
