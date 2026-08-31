import type { CurrentUser, TenantRole } from '../../api/platform';

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
    ...overrides,
  };
}
