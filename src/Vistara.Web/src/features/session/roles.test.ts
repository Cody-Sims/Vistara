import { describe, expect, it } from 'vitest';
import type { CurrentUser, TenantMembership } from '../../api/platform';
import {
  activeMembership,
  canAdminister,
  credentialKind,
  describeRole,
  hasScope,
  sessionScopes,
} from './roles';

function membership(
  id: string,
  role: TenantMembership['role'],
  membershipStatus: TenantMembership['membershipStatus'] = 'Active',
): TenantMembership {
  return { id, slug: id, name: id, role, membershipStatus };
}

function user(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userId: 'user-1',
    email: 'ada@example.test',
    displayName: 'Ada Lovelace',
    tenantId: 'tenant-a',
    role: 'TenantAdmin',
    tenants: [membership('tenant-a', 'TenantAdmin')],
    csrfHeaderName: 'X-Vistara-CSRF',
    csrfToken: 'csrf-token-1',
    ...overrides,
  };
}

/** The same account reached with an API key: no antiforgery token is issued. */
function tenantBound(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return { ...user(overrides), csrfToken: undefined };
}

describe('active membership', () => {
  it('selects the membership for the tenant the session is scoped to', () => {
    const resolved = activeMembership(
      user({
        tenantId: 'tenant-b',
        role: 'Viewer',
        tenants: [
          membership('tenant-a', 'TenantOwner'),
          membership('tenant-b', 'Viewer'),
        ],
      }),
    );

    expect(resolved?.id).toBe('tenant-b');
    expect(resolved?.role).toBe('Viewer');
  });

  it('has no membership when the active tenant is absent from the list', () => {
    expect(
      activeMembership(
        user({
          tenantId: 'tenant-z',
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toBeUndefined();
  });

  it('has no membership when the session carries no tenant', () => {
    expect(
      activeMembership(
        user({
          tenantId: undefined,
          role: undefined,
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toBeUndefined();
  });
});

describe('administration', () => {
  it('admits an administrator of the active tenant', () => {
    expect(canAdminister(user())).toBe(true);
  });

  it('never borrows administration from another tenant', () => {
    expect(
      canAdminister(
        user({
          tenantId: 'tenant-b',
          role: 'Member',
          tenants: [
            membership('tenant-a', 'TenantOwner'),
            membership('tenant-b', 'Member'),
          ],
        }),
      ),
    ).toBe(false);
  });

  it('refuses when the session role disagrees with the membership role', () => {
    expect(
      canAdminister(
        user({
          role: 'Member',
          tenants: [membership('tenant-a', 'TenantAdmin')],
        }),
      ),
    ).toBe(false);
  });

  it('refuses a membership that is not active', () => {
    expect(
      canAdminister(
        user({ tenants: [membership('tenant-a', 'TenantAdmin', 'Suspended')] }),
      ),
    ).toBe(false);
  });

  it('refuses an unmatched or missing tenant', () => {
    expect(
      canAdminister(
        user({ tenantId: 'tenant-z', tenants: [membership('tenant-a', 'TenantOwner')] }),
      ),
    ).toBe(false);
    expect(canAdminister(undefined)).toBe(false);
  });

  it('names each role for the interface', () => {
    expect(describeRole('TenantOwner')).toBe('Owner');
    expect(describeRole('TenantAdmin')).toBe('Administrator');
    expect(describeRole('Member')).toBe('Member');
    expect(describeRole('Viewer')).toBe('Viewer');
    expect(describeRole(undefined)).toBe('Guest');
  });
});

describe('credential kind', () => {
  it('reads an interactive cookie session from its antiforgery token', () => {
    expect(credentialKind(user())).toBe('browser');
  });

  it('reads a credential without an antiforgery token as tenant-bound', () => {
    expect(credentialKind(tenantBound())).toBe('tenantBound');
    expect(credentialKind(undefined)).toBe('tenantBound');
  });
});

describe('session scopes', () => {
  it('grants a cookie session the scopes its membership role carries', () => {
    expect(sessionScopes(user({ role: 'TenantOwner', tenants: [membership('tenant-a', 'TenantOwner')] })))
      .toEqual(
        expect.arrayContaining(['assets.read', 'members.manage', 'quotas.manage']),
      );
    expect(sessionScopes(user())).not.toContain('quotas.manage');
    expect(
      sessionScopes(user({ role: 'Member', tenants: [membership('tenant-a', 'Member')] })),
    ).not.toContain('members.manage');
  });

  it('assumes no scope for a tenant-bound credential, whatever role it reports', () => {
    expect(
      sessionScopes(
        tenantBound({
          role: 'TenantOwner',
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toEqual([]);
    expect(hasScope(tenantBound(), 'members.manage')).toBe(false);
    expect(hasScope(user(), 'members.manage')).toBe(true);
  });

  it('grants nothing for a membership that is not active', () => {
    expect(
      sessionScopes(user({ tenants: [membership('tenant-a', 'TenantAdmin', 'Suspended')] })),
    ).toEqual([]);
  });
});

describe('administration with a tenant-bound credential', () => {
  it('refuses an owner reported by an API key that cannot administer', () => {
    expect(
      canAdminister(
        tenantBound({
          role: 'TenantOwner',
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toBe(false);
  });

  it('still admits the same owner on an interactive session', () => {
    expect(
      canAdminister(
        user({
          role: 'TenantOwner',
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toBe(true);
  });
});
