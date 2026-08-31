import type {
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
 * How the API authenticated this session. `GET /api/v1/me` issues `csrfToken`
 * only to an interactive cookie session, so a credential that arrives without
 * one is tenant-bound: an API key or another automation credential whose own
 * scopes the contract never publishes.
 */
export type CredentialKind = 'browser' | 'tenantBound';

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

export function credentialKind(user: CurrentUser | undefined): CredentialKind {
  return user?.csrfToken ? 'browser' : 'tenantBound';
}

/**
 * What this session may ask the API for. A tenant-bound credential carries the
 * scopes of the key it was issued for, which `GET /api/v1/me` does not
 * publish, so none are assumed: the role it reports is not authority on its
 * own and the administration screens it would open answer `403`.
 */
export function sessionScopes(
  user: CurrentUser | undefined,
): readonly SessionScope[] {
  const membership = activeMembership(user);
  if (
    membership === undefined ||
    membership.membershipStatus !== 'Active' ||
    credentialKind(user) !== 'browser' ||
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
  return kind === 'browser' ? 'Signed-in session' : 'Workspace credential';
}
