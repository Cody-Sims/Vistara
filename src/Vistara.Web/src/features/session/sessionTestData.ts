import type { CurrentUser, TenantRole } from '../../api/platform';

/**
 * An interactive cookie session, which `GET /api/v1/me` publishes as
 * `authenticationKind: 'cookie'`.
 */
export function currentUser(
  overrides: Partial<CurrentUser> = {},
  role: TenantRole = 'Member',
): CurrentUser {
  return {
    userId: 'user-1',
    email: 'ada@example.test',
    displayName: 'Ada Lovelace',
    tenantId: 'tenant-a',
    role,
    tenants: [
      {
        id: 'tenant-a',
        slug: 'studio',
        name: 'Studio',
        role,
        membershipStatus: 'Active',
      },
    ],
    csrfHeaderName: 'X-Vistara-CSRF',
    csrfToken: 'csrf-token-1',
    authenticationKind: 'cookie',
    ...overrides,
  };
}

/**
 * A credential bound to one tenant. `GET /api/v1/me` still names the
 * membership role, and publishes the credential that carried the request.
 */
export function tenantBoundUser(
  overrides: Partial<CurrentUser> = {},
  role: TenantRole = 'TenantOwner',
): CurrentUser {
  return currentUser(
    { csrfToken: undefined, authenticationKind: 'apiKey', ...overrides },
    role,
  );
}
