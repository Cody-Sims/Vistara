import type { CurrentUser, TenantRole } from '../../api/platform';

/**
 * An interactive cookie session, which `GET /api/v1/me` answers with an
 * antiforgery token.
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
    ...overrides,
  };
}

/**
 * A tenant-bound credential such as an API key. `GET /api/v1/me` still names
 * the membership role, but issues no antiforgery token because the principal
 * is not an interactive browser session.
 */
export function tenantBoundUser(
  overrides: Partial<CurrentUser> = {},
  role: TenantRole = 'TenantOwner',
): CurrentUser {
  return { ...currentUser(overrides, role), csrfToken: undefined };
}
