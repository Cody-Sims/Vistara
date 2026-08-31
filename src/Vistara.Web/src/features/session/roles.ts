import type {
  AuthenticationKind,
  CurrentUser,
  TenantMembership,
  TenantRole,
} from '../../api/platform';

const administrativeRoles: readonly TenantRole[] = [
  'TenantOwner',
  'TenantAdmin',
];

/** Every scope the API authorizes a request with. */
export type SessionScope =
  | 'assets.read'
  | 'assets.upload'
  | 'metadata.manage'
  | 'shares.manage'
  | 'members.manage'
  | 'api_keys.manage'
  | 'quotas.manage';

/**
 * The credential this session was authenticated with. `unknown` covers an
 * anonymous session and a deployment that does not publish
 * `authenticationKind`; neither is treated as interactive.
 */
export type CredentialKind = AuthenticationKind | 'unknown';

/**
 * Scopes the API derives from a membership role for a cookie session. This
 * mirrors the published catalogue rather than widening it: nothing here opens
 * a screen the server would refuse.
 */
const browserScopes: Record<TenantRole, readonly SessionScope[]> = {
  Viewer: ['assets.read'],
  Member: ['assets.read', 'assets.upload', 'metadata.manage', 'shares.manage'],
  TenantAdmin: [
    'assets.read',
    'assets.upload',
    'metadata.manage',
    'shares.manage',
    'members.manage',
    'api_keys.manage',
  ],
  TenantOwner: [
    'assets.read',
    'assets.upload',
    'metadata.manage',
    'shares.manage',
    'members.manage',
    'api_keys.manage',
    'quotas.manage',
  ],
};

/** Scopes that open the administration section at all. */
const administrationScopes: readonly SessionScope[] = [
  'members.manage',
  'quotas.manage',
];

/**
 * The membership the session is scoped to. A session without `tenantId`, or
 * one whose tenant is not in the membership list, has no active membership:
 * another tenant's role never stands in for it.
 */
export function activeMembership(
  user: CurrentUser | undefined,
): TenantMembership | undefined {
  if (!user?.tenantId) {
    return undefined;
  }

  return user.tenants.find(
    (membership) => membership.id === user.tenantId,
  );
}

/**
 * Reads the credential the API named. It is never guessed from `csrfToken`:
 * that token is transient, so a cookie session between issues would be
 * demoted and a key that carried one would be promoted.
 */
export function credentialKind(user: CurrentUser | undefined): CredentialKind {
  switch (user?.authenticationKind) {
    case 'cookie':
    case 'apiKey':
    case 'bearer':
      return user.authenticationKind;
    default:
      return 'unknown';
  }
}

/**
 * What this session may ask the API for. Only an interactive cookie session
 * carries the scopes of its membership role. A key or token carries the scopes
 * it was issued with, which `GET /api/v1/me` does not publish, so none are
 * assumed: the role it reports is not authority on its own and the
 * administration screens it would open answer `403`.
 */
export function sessionScopes(
  user: CurrentUser | undefined,
): readonly SessionScope[] {
  const membership = activeMembership(user);
  if (
    membership === undefined ||
    membership.membershipStatus !== 'Active' ||
    credentialKind(user) !== 'cookie' ||
    (user?.role !== undefined && user.role !== membership.role)
  ) {
    return [];
  }

  return browserScopes[membership.role];
}

export function hasScope(
  user: CurrentUser | undefined,
  scope: SessionScope,
): boolean {
  return sessionScopes(user).includes(scope);
}

/** Whether the administration section is reachable for this session at all. */
export function canAdminister(user: CurrentUser | undefined): boolean {
  const membership = activeMembership(user);
  if (
    membership === undefined ||
    !administrativeRoles.includes(membership.role)
  ) {
    return false;
  }

  const scopes = sessionScopes(user);
  return administrationScopes.some((scope) => scopes.includes(scope));
}

export function describeRole(role: TenantRole | undefined): string {
  switch (role) {
    case 'TenantOwner':
      return 'Owner';
    case 'TenantAdmin':
      return 'Administrator';
    case 'Member':
      return 'Member';
    case 'Viewer':
      return 'Viewer';
    default:
      return 'Guest';
  }
}

/**
 * The static preview has no API and no session, so its shell is rendered as
 * the owner it is previewing rather than as an unscoped visitor.
 */
export const previewScopes: readonly SessionScope[] =
  browserScopes.TenantOwner;

/** How the session reached the API, in words the interface can show. */
export function describeCredential(kind: CredentialKind): string {
  switch (kind) {
    case 'cookie':
      return 'Signed-in session';
    case 'apiKey':
      return 'API key';
    case 'bearer':
      return 'Bearer token';
    default:
      return 'Unrecognised credential';
  }
}
