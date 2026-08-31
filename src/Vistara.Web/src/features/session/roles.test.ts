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
    authenticationKind: 'cookie',
    ...overrides,
  };
}

/** The same account reached with a credential bound to one tenant. */
function tenantBound(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return user({
    csrfToken: undefined,
    authenticationKind: 'apiKey',
    ...overrides,
  });
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
  it('reports the credential the API says authenticated the session', () => {
    expect(credentialKind(user())).toBe('cookie');
    expect(credentialKind(tenantBound())).toBe('apiKey');
    expect(
      credentialKind(tenantBound({ authenticationKind: 'bearer' })),
    ).toBe('bearer');
  });

  it('reports an unpublished or unknown credential as unknown', () => {
    expect(credentialKind(undefined)).toBe('unknown');
    expect(credentialKind(user({ authenticationKind: undefined }))).toBe(
      'unknown',
    );
    expect(
      credentialKind(
        user({
          authenticationKind: 'passkey' as CurrentUser['authenticationKind'],
        }),
      ),
    ).toBe('unknown');
  });

  it('never reads the credential from the antiforgery token', () => {
    // A cookie session between token issues, and a key that somehow carries
    // one, are both named by the contract rather than guessed at.
    expect(credentialKind(user({ csrfToken: undefined }))).toBe('cookie');
    expect(credentialKind(tenantBound({ csrfToken: 'borrowed' }))).toBe(
      'apiKey',
    );
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
    for (const kind of ['apiKey', 'bearer'] as const) {
      expect(
        sessionScopes(
          tenantBound({
            authenticationKind: kind,
            role: 'TenantOwner',
            tenants: [membership('tenant-a', 'TenantOwner')],
          }),
        ),
      ).toEqual([]);
    }

    expect(hasScope(tenantBound(), 'members.manage')).toBe(false);
    expect(hasScope(user(), 'members.manage')).toBe(true);
  });

  it('grants nothing when the credential kind was not published', () => {
    expect(sessionScopes(user({ authenticationKind: undefined }))).toEqual([]);
  });

  it('grants a cookie session its scopes with no antiforgery token held', () => {
    expect(
      sessionScopes(
        user({
          csrfToken: undefined,
          role: 'TenantOwner',
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toContain('quotas.manage');
  });

  it('grants nothing for a membership that is not active', () => {
    expect(
      sessionScopes(user({ tenants: [membership('tenant-a', 'TenantAdmin', 'Suspended')] })),
    ).toEqual([]);
  });
});

describe('administration with a tenant-bound credential', () => {
  it('refuses an owner reported by a key or token that cannot administer', () => {
    for (const kind of ['apiKey', 'bearer'] as const) {
      expect(
        canAdminister(
          tenantBound({
            authenticationKind: kind,
            role: 'TenantOwner',
            tenants: [membership('tenant-a', 'TenantOwner')],
          }),
        ),
      ).toBe(false);
    }
  });

  it('keeps an owner administering while their antiforgery token is absent', () => {
    expect(
      canAdminister(
        user({
          csrfToken: undefined,
          role: 'TenantOwner',
          tenants: [membership('tenant-a', 'TenantOwner')],
        }),
      ),
    ).toBe(true);
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
