import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../generated/client';
import { PlatformApiClient } from './platformClient';

function jsonResponse(body: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

function problemResponse(status: number, code: string) {
  return new Response(
    JSON.stringify({
      type: `https://vistara.dev/problems/${code}`,
      title: code,
      status,
      code,
      errors: {},
    }),
    {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    },
  );
}

const sessionBody = {
  user: {
    id: 'user-1',
    displayName: 'Ada',
    email: 'ada@example.test',
    platformAdmin: false,
  },
  memberships: [
    {
      tenantId: 'tenant-1',
      tenantName: 'Studio',
      role: 'TenantAdmin',
      status: 'active',
    },
  ],
  activeTenantId: 'tenant-1',
  preferences: { theme: 'system', locale: 'en-US' },
  antiforgeryToken: 'token-1',
};

describe('platform session client', () => {
  it('reads the current session without sending cross-origin credentials', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse(sessionBody),
    );
    const client = new PlatformApiClient({ fetch });

    const session = await client.getSession();

    expect(fetch).toHaveBeenCalledTimes(1);
    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe('/api/v1/me');
    expect(init?.method).toBe('GET');
    expect(init?.credentials).toBe('same-origin');
    expect(session.user.displayName).toBe('Ada');
    expect(session.memberships[0]?.role).toBe('TenantAdmin');
  });

  it('signs in and rotates the antiforgery token used by later mutations', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        jsonResponse({ ...sessionBody, antiforgeryToken: 'token-2' }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.login({
      email: 'ada@example.test',
      password: 'correct horse',
      rememberMe: true,
    });
    await client.logout();

    const [loginUrl, loginInit] = fetch.mock.calls[0]!;
    expect(loginUrl).toBe('/api/v1/auth/login');
    expect(loginInit?.method).toBe('POST');
    expect(JSON.parse(String(loginInit?.body))).toEqual({
      email: 'ada@example.test',
      password: 'correct horse',
      rememberMe: true,
    });

    const [logoutUrl, logoutInit] = fetch.mock.calls[1]!;
    expect(logoutUrl).toBe('/api/v1/auth/logout');
    expect(new Headers(logoutInit?.headers).get('X-CSRF-TOKEN')).toBe(
      'token-2',
    );
  });

  it('never retains the submitted password after a sign-in attempt', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      problemResponse(401, 'invalid_credentials'),
    );
    const client = new PlatformApiClient({ fetch });

    await expect(
      client.login({ email: 'ada@example.test', password: 'wrong' }),
    ).rejects.toMatchObject({ status: 401 });
    expect(JSON.stringify(client)).not.toContain('wrong');
  });

  it('maps problem details onto the shared API error type', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      problemResponse(403, 'forbidden'),
    );
    const client = new PlatformApiClient({ fetch });

    const error = await client.getSession().catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(VistaraApiError);
    expect((error as VistaraApiError).status).toBe(403);
    expect((error as VistaraApiError).problem.code).toBe('forbidden');
  });

  it('reads deployment capabilities including configured sign-in providers', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({
        database: 'postgres',
        authentication: {
          localAccounts: true,
          oidc: { displayName: 'Corp SSO', startPath: '/api/v1/auth/oidc' },
        },
      }),
    );
    const client = new PlatformApiClient({ fetch });

    const capabilities = await client.getCapabilities();

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/capabilities');
    expect(capabilities.authentication?.oidc?.displayName).toBe('Corp SSO');
  });
});

describe('platform administration client', () => {
  it('encodes list queries for users, jobs, and audit records', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ items: [] }),
    );
    const client = new PlatformApiClient({ fetch });

    await client.listAdminUsers({ limit: 25, search: 'ada k' });
    await client.listAdminJobs({ states: ['failed', 'dead'] });
    await client.listAuditEvents({ cursor: 'c1', action: 'share.created' });

    expect(fetch.mock.calls[0]![0]).toBe(
      '/api/v1/admin/users?limit=25&search=ada+k',
    );
    expect(fetch.mock.calls[1]![0]).toBe(
      '/api/v1/admin/jobs?states=failed&states=dead',
    );
    expect(fetch.mock.calls[2]![0]).toBe(
      '/api/v1/admin/audit?cursor=c1&action=share.created',
    );
  });

  it('sends concurrency and antiforgery headers for administrative changes', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(sessionBody))
      .mockResolvedValueOnce(
        jsonResponse(
          {
            id: 'user-2',
            displayName: 'Grace',
            email: 'grace@example.test',
            role: 'Member',
            status: 'active',
            createdAt: '2026-01-01T00:00:00Z',
            version: 4,
          },
          { headers: { ETag: '"4"', 'Content-Type': 'application/json' } },
        ),
      );
    const client = new PlatformApiClient({ fetch });

    await client.getSession();
    const updated = await client.updateAdminUser(
      'user-2',
      { role: 'Member' },
      { ifMatch: '"3"' },
    );

    const [url, init] = fetch.mock.calls[1]!;
    expect(url).toBe('/api/v1/admin/users/user-2');
    expect(init?.method).toBe('PATCH');
    const headers = new Headers(init?.headers);
    expect(headers.get('If-Match')).toBe('"3"');
    expect(headers.get('X-CSRF-TOKEN')).toBe('token-1');
    expect(headers.get('Content-Type')).toBe('application/json');
    expect(updated.etag).toBe('"4"');
    expect(updated.data.role).toBe('Member');
  });

  it('surfaces policy edit conflicts as a versioned failure', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      problemResponse(409, 'version_conflict'),
    );
    const client = new PlatformApiClient({ fetch });

    const error = await client
      .updatePolicies(
        { retention: { trashRetentionDays: 30 } },
        { ifMatch: '"7"' },
      )
      .catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(VistaraApiError);
    expect((error as VistaraApiError).status).toBe(409);
  });

  it('escapes identifiers in administrative paths', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ id: 'job/1', kind: 'derivatives', state: 'queued' }),
    );
    const client = new PlatformApiClient({ fetch });

    await client.retryJob('job/1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/admin/jobs/job%2F1/retry');
  });
});
