// Entra application registration for the Vistara hosted API sign-in path.
//
// Deployed through the Microsoft Graph Bicep extension pinned in
// `deploy/azure/bicepconfig.json`. `uniqueName` is the Graph alternate key, so
// repeated deployments upsert the same application instead of creating a new
// one: this module is idempotent by construction and never emits a credential.
//
// Scope: the caller (`infra/main.bicep`, owned by HB-08) is subscription
// scoped, so it must invoke this module with `scope: resourceGroup(...)`.
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
//     --enable-id-token-issuance false --enable-access-token-issuance false
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
// the values this module computes.

targetScope = 'resourceGroup'

extension microsoftGraphV1

@description('Graph alternate key for the application; stable across deployments so redeploys upsert rather than duplicate. Expected shape: vistara-<environmentName>.')
@minLength(3)
@maxLength(120)
param uniqueName string

@description('Human readable name shown on the Entra consent and sign-in screens.')
@minLength(1)
@maxLength(256)
param displayName string

@description('Public HTTPS host of the API container app, without scheme or trailing slash. The registered redirect URI must match the runtime Platform:Authentication:Oidc RedirectUri byte for byte.')
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
// substitution cannot silently produce a redirect URI or federated credential
// subject that no longer matches at runtime. Host names and directory GUIDs are
// lowercased to their canonical Entra form; the display name keeps its casing.
var normalizedUniqueName = toLower(trim(uniqueName))
var normalizedDisplayName = trim(displayName)
var normalizedApiFqdn = toLower(trim(apiFqdn))
var normalizedApiIdentityPrincipalId = toLower(trim(apiIdentityPrincipalId))
var normalizedTenantId = toLower(trim(tenantId))

var apiBaseUri = 'https://${normalizedApiFqdn}'
var redirectUri = '${apiBaseUri}/api/v1/auth/oidc/entra/callback'
var signOutUri = '${apiBaseUri}/api/v1/auth/logout'

var federatedCredentialName = 'api-managed-identity'
#disable-next-line no-hardcoded-env-urls // The v2.0 issuer is a protocol constant that Entra matches exactly; it cannot come from `environment()`.
var federatedCredentialIssuer = 'https://login.microsoftonline.com/${normalizedTenantId}/v2.0'
var federatedCredentialAudience = 'api://AzureADTokenExchange'

// Microsoft Graph first-party app ID and the well-known delegated scope IDs for
// the only three claims the sign-in path consumes.
var microsoftGraphAppId = '00000003-0000-0000-c000-000000000000'
var openIdScopeId = '37f7f235-527c-4136-accd-4a02d197296e'
var profileScopeId = '14dad69e-099b-42c9-810b-d002981feec1'
var emailScopeId = '64a6cdd6-aab1-7aab-01b2-9bfd0e2391de'

resource application 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: normalizedUniqueName
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
    logoutUrl: signOutUri
    redirectUris: [
      redirectUri
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
  issuer: federatedCredentialIssuer
  subject: normalizedApiIdentityPrincipalId
  audiences: [
    federatedCredentialAudience
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
