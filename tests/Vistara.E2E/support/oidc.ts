import path from 'node:path';
import { suiteRateLimits } from './deployment.js';
import { artifactsDirectory } from './paths.js';

/**
 * The hosted sign-in environment this suite runs against.
 *
 * It is a second API instance with its own database, pointed at a stub
 * identity provider on the HTTPS loopback interface. It is separate from the
 * gallery instance on purpose: hosted sign-in changes what the sign-in page
 * offers and what signing out does, and none of the other suites should have
 * to be read in light of a provider they do not use. The dedicated database
 * also leaves first-owner bootstrap unclaimed, so one run can prove both that
 * an existing member signs in and that an allowlisted directory identity can
 * claim the workspace.
 *
 * Every identifier here is fixed so a failed run can be read afterwards. None
 * of them is a secret: the client secret is accepted only by the stub provider
 * this run starts, and the password only by the throwaway database it seeds.
 */
export const oidcEnvironment = {
  baseUrl: process.env.VISTARA_E2E_OIDC_BASE_URL ?? 'https://127.0.0.1:5189',
  identityProviderPort: 5190,
  get identityProviderUrl() {
    return `https://127.0.0.1:${this.identityProviderPort}`;
  },
  providerId: 'entra',
  providerDisplayName: 'Microsoft Entra ID',
  directoryTenantId: '3f4c8b7a-9d21-4c6e-bf10-2a5d7e9c4801',
  /** A directory that is not the one this deployment accepts. */
  foreignDirectoryTenantId: '9e8d7c6b-5a49-4382-b1c0-fedcba987654',
  clientId: '8b2f4c61-7d93-4a58-9e21-6c0f5b3d7a44',
  clientSecret: 'e2e-stub-client-secret',
  /** The directory identity already linked to the seeded member. */
  memberObjectId: '5c9d3e21-4f77-4b8a-9d55-1e2a6b7c8d90',
  /** The one directory identity allowed to claim the first workspace. */
  allowedOwnerObjectId: '7a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d',
  /** A directory identity that is neither a member nor allowlisted. */
  strangerObjectId: '2b3c4d5e-6f70-4812-9a3b-4c5d6e7f8091',
  tenantSlug: 'e2e-oidc',
  bootstrapTenantSlug: 'vistara',
  login: 'member@e2e.invalid',
  password: 'E2E-oidc-password-1',
} as const;

export const oidcDatabasePath = path.join(artifactsDirectory, 'vistara-oidc.db');
export const oidcMediaRoot = path.join(artifactsDirectory, 'oidc-media');
export const oidcCertificatePath = path.join(
  artifactsDirectory,
  'stub-identity-provider.cer',
);
/**
 * Where the hosted sign-in API publishes the public half of its own run
 * certificate, so the probe that waits for it can pin that one certificate.
 */
export const oidcApiCertificatePath = path.join(
  artifactsDirectory,
  'vistara-e2e-oidc-api.cer',
);

export const oidcRoutes = {
  callbackPath: '/api/v1/auth/oidc/entra/callback',
  signedOutPath: '/api/v1/auth/oidc/entra/signed-out',
  startPath: '/api/v1/auth/oidc/entra/start',
} as const;

/** The configuration the OIDC API instance is started with. */
export function oidcApiEnvironment(): Record<string, string> {
  return {
    Persistence__Provider: 'Sqlite',
    Persistence__ConnectionString: `Data Source=${oidcDatabasePath}`,
    Media__Storage__Provider: 'Local',
    Media__Storage__Local__RootPath: oidcMediaRoot,
    Media__Imaging__Provider: 'NetVips',
    ASPNETCORE_ENVIRONMENT: 'Development',
    Logging__LogLevel__Microsoft: 'Warning',
    Platform__Authentication__ApiKeys__CurrentPepperVersion: 'v1',
    Platform__Authentication__ApiKeys__Peppers__v1:
      'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
    Platform__Authentication__Jwt__Issuers__0__ProfileId: 'e2e',
    Platform__Authentication__Jwt__Issuers__0__Issuer:
      'https://issuer.e2e.invalid',
    Platform__Authentication__Jwt__Issuers__0__Audience: 'vistara-api',
    Platform__Authentication__Jwt__Issuers__0__MetadataAddress:
      'https://issuer.e2e.invalid/.well-known/openid-configuration',
    Platform__Authentication__Jwt__Issuers__0__AllowedAlgorithms__0: 'RS256',
    Platform__Authentication__Oidc__Enabled: 'true',
    Platform__Authentication__Oidc__Providers__0__ProviderId:
      oidcEnvironment.providerId,
    Platform__Authentication__Oidc__Providers__0__DisplayName:
      oidcEnvironment.providerDisplayName,
    Platform__Authentication__Oidc__Providers__0__TenantId:
      oidcEnvironment.directoryTenantId,
    Platform__Authentication__Oidc__Providers__0__ClientId:
      oidcEnvironment.clientId,
    Platform__Authentication__Oidc__Providers__0__ClientSecret:
      oidcEnvironment.clientSecret,
    Platform__Authentication__Oidc__Providers__0__Authority:
      `${oidcEnvironment.identityProviderUrl}/${oidcEnvironment.directoryTenantId}/v2.0`,
    Platform__Authentication__Oidc__Providers__0__RedirectUri: `${oidcEnvironment.baseUrl}${oidcRoutes.callbackPath}`,
    Platform__Authentication__Oidc__Providers__0__ApplicationBaseUri: `${oidcEnvironment.baseUrl}/`,
    Platform__Authentication__Oidc__Providers__0__PostLogoutRedirectUri: `${oidcEnvironment.baseUrl}${oidcRoutes.signedOutPath}`,
    Platform__Bootstrap__FirstOwner__Enabled: 'true',
    Platform__Bootstrap__FirstOwner__ProviderId: oidcEnvironment.providerId,
    Platform__Bootstrap__FirstOwner__DirectoryTenantId:
      oidcEnvironment.directoryTenantId,
    Platform__Bootstrap__FirstOwner__TenantSlug:
      oidcEnvironment.bootstrapTenantSlug,
    Platform__Bootstrap__FirstOwner__TenantName: 'Vistara',
    Platform__Bootstrap__FirstOwner__AllowedObjectIds__0:
      oidcEnvironment.allowedOwnerObjectId,
    // The API trusts exactly the certificate the stub provider wrote for this
    // run, and nothing else about the transport changes. The application is
    // itself served over TLS with a certificate it generates per run, so the
    // reply URLs above are https and RequireHttps keeps its shipped value.
    VISTARA_E2E_OIDC_CERTIFICATE: oidcCertificatePath,
    VISTARA_E2E_SERVER_CERTIFICATE: oidcApiCertificatePath,
    ...suiteRateLimits,
  };
}

/** The arguments the stub identity provider is started with. */
export function identityProviderArguments(): string[] {
  return [
    'idp',
    '--port',
    String(oidcEnvironment.identityProviderPort),
    '--directory-tenant',
    oidcEnvironment.directoryTenantId,
    '--client-id',
    oidcEnvironment.clientId,
    '--client-secret',
    oidcEnvironment.clientSecret,
    '--redirect-uri',
    `${oidcEnvironment.baseUrl}${oidcRoutes.callbackPath}`,
    '--post-logout-redirect-uri',
    `${oidcEnvironment.baseUrl}${oidcRoutes.signedOutPath}`,
    '--certificate',
    oidcCertificatePath,
  ];
}

/** The arguments that seed the workspace hosted sign-in signs in to. */
export function oidcSeedArguments(): string[] {
  return [
    'seed-oidc',
    '--database',
    oidcDatabasePath,
    '--media-root',
    oidcMediaRoot,
    '--directory-tenant',
    oidcEnvironment.directoryTenantId,
    '--provider',
    oidcEnvironment.providerId,
    '--object-id',
    oidcEnvironment.memberObjectId,
    '--login',
    oidcEnvironment.login,
    '--password',
    oidcEnvironment.password,
    '--tenant-slug',
    oidcEnvironment.tenantSlug,
  ];
}
