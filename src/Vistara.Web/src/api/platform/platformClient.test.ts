import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../generated/client';
import { isStaleVersion, isStateConflict, versionTag } from '../versionTag';
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
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

const currentUser = {
  userId: '0195f0d4-0000-7000-8000-000000000001',
  email: 'ada@example.test',
  displayName: 'Ada Lovelace',
  tenantId: '0195f0d4-0000-7000-8000-0000000000a1',
  role: 'TenantAdmin',
  tenants: [
    {
      id: '0195f0d4-0000-7000-8000-0000000000a1',
      slug: 'studio',
      name: 'Studio',
      role: 'TenantAdmin',
      membershipStatus: 'Active',
    },
  ],
  csrfHeaderName: 'X-Vistara-CSRF',
};

describe('platform session client', () => {
  it('reads the current user from the session route', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse(currentUser),
    );
    const client = new PlatformApiClient({ fetch });

    const user = await client.getSession();

    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe('/api/v1/me');
    expect(init?.method).toBe('GET');
    expect(init?.credentials).toBe('same-origin');
    expect(user.displayName).toBe('Ada Lovelace');
    expect(user.tenants[0]?.membershipStatus).toBe('Active');
  });

  it('signs in with the login field the API accepts', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ user: currentUser, csrfToken: 'token-2' }),
    );
    const client = new PlatformApiClient({ fetch });

    const session = await client.login({
      login: 'ada@example.test',
      password: 'correct horse',
    });

    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe('/api/v1/auth/login');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      login: 'ada@example.test',
      password: 'correct horse',
    });
    expect(session.user.email).toBe('ada@example.test');
  });

  it('sends the antiforgery token under the header the API published', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        jsonResponse({
          user: { ...currentUser, csrfHeaderName: 'X-Deployment-CSRF' },
          csrfToken: 'token-3',
        }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.login({ login: 'ada@example.test', password: 'pw' });
    await client.logout();

    const headers = new Headers(fetch.mock.calls[1]![1]?.headers);
    expect(fetch.mock.calls[1]![0]).toBe('/api/v1/auth/logout');
    expect(headers.get('X-Deployment-CSRF')).toBe('token-3');
    expect(headers.get('X-Vistara-CSRF')).toBeNull();
  });

  it('forgets the antiforgery token after signing out', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        jsonResponse({ user: currentUser, csrfToken: 'token-4' }),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.login({ login: 'ada@example.test', password: 'pw' });
    await client.logout();
    await client.logout();

    expect(
      new Headers(fetch.mock.calls[2]![1]?.headers).get('X-Vistara-CSRF'),
    ).toBeNull();
  });

  it('reads the deployment capability document', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({
        schemaVersion: 1,
        database: { provider: 'postgres' },
        storage: { provider: 's3', maxObjectBytes: 100 },
        imaging: { provider: 'skia', outputFormats: ['webp'] },
        upload: { maxBytes: 100, multipartUpload: true },
        search: { text: true, timeline: true },
        api: { defaultPageSize: 50, maxPageSize: 200 },
      }),
    );
    const client = new PlatformApiClient({ fetch });

    const capabilities = await client.getCapabilities();

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/capabilities');
    expect(capabilities.schemaVersion).toBe(1);
    expect(capabilities.storage.provider).toBe('s3');
  });

  it('maps problem details onto the shared API error type', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      problemResponse(403, 'auth.forbidden'),
    );
    const client = new PlatformApiClient({ fetch });

    const error = await client.getSession().catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(VistaraApiError);
    expect((error as VistaraApiError).status).toBe(403);
    expect((error as VistaraApiError).problem.code).toBe('auth.forbidden');
  });
});

describe('platform tenant administration client', () => {
  it('lists tenants and members from the tenant routes', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ items: [] }),
    );
    const client = new PlatformApiClient({ fetch });

    await client.listTenants();
    await client.listTenantMembers('0195f0d4-0000-7000-8000-0000000000a1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/tenants');
    expect(fetch.mock.calls[1]![0]).toBe(
      '/api/v1/tenants/0195f0d4-0000-7000-8000-0000000000a1/members',
    );
  });

  it('invites a member with the antiforgery header and encoded identifier', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(
        jsonResponse({ user: currentUser, csrfToken: 'token-5' }),
      )
      .mockResolvedValueOnce(
        jsonResponse(
          {
            userId: 'user-2',
            email: 'grace@example.test',
            displayName: 'Grace Hopper',
            role: 'Member',
            status: 'Invited',
            invitedAt: '2026-02-01T00:00:00Z',
            version: 1,
          },
          { status: 201 },
        ),
      );
    const client = new PlatformApiClient({ fetch });

    await client.login({ login: 'ada@example.test', password: 'pw' });
    await client.inviteTenantMember('tenant/1', {
      email: 'grace@example.test',
      role: 'Member',
    });

    const [url, init] = fetch.mock.calls[1]!;
    expect(url).toBe('/api/v1/tenants/tenant%2F1/members');
    expect(init?.method).toBe('POST');
    const headers = new Headers(init?.headers);
    expect(headers.get('X-Vistara-CSRF')).toBe('token-5');
    expect(headers.get('Content-Type')).toBe('application/json');
  });

  it('manages API keys through the published routes', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ items: [] }),
    );
    const client = new PlatformApiClient({ fetch });

    await client.listApiKeys();
    await client.createApiKey({ scopes: ['assets.read'] });
    await client.revokeApiKey('key 1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/api-keys');
    expect(fetch.mock.calls[1]![1]?.method).toBe('POST');
    expect(fetch.mock.calls[2]![0]).toBe('/api/v1/api-keys/key%201');
    expect(fetch.mock.calls[2]![1]?.method).toBe('DELETE');
  });

  it('reads a single job by identifier', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({
        id: 'job-1',
        type: 'derivatives',
        state: 'Failed',
        attempts: 3,
        maxAttempts: 5,
        createdAt: '2026-02-01T00:00:00Z',
        availableAt: '2026-02-01T00:05:00Z',
        version: 4,
      }),
    );
    const client = new PlatformApiClient({ fetch });

    const job = await client.getJob('job/1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/jobs/job%2F1');
    expect(job.state).toBe('Failed');
  });

  it('exposes only the routes the API branch publishes', () => {
    const client = new PlatformApiClient();
    const methods = Object.getOwnPropertyNames(
      Object.getPrototypeOf(client) as object,
    ).filter((name) => name !== 'constructor');

    expect(methods.sort()).toEqual(
      [
        'antiforgeryHeaderName',
        'createApiKey',
        'getCapabilities',
        'getJob',
        'getSession',
        'inviteTenantMember',
        'listApiKeys',
        'listTenantMembers',
        'listTenants',
        'login',
        'logout',
        'revokeApiKey',
      ].sort(),
    );
  });
});

describe('optimistic concurrency helpers', () => {
  it('formats entity tags the way every Vistara route emits them', () => {
    expect(versionTag(7)).toBe('"v7"');
  });

  it('separates a stale precondition from a state conflict', () => {
    const stale = new VistaraApiError(412, {
      type: 'about:blank',
      title: 'Precondition Failed',
      status: 412,
      code: 'precondition_failed',
      errors: {},
    });
    const conflict = new VistaraApiError(409, {
      type: 'about:blank',
      title: 'Conflict',
      status: 409,
      code: 'conflict',
      errors: {},
    });

    expect(isStaleVersion(stale)).toBe(true);
    expect(isStaleVersion(conflict)).toBe(false);
    expect(isStateConflict(conflict)).toBe(true);
    expect(isStateConflict(stale)).toBe(false);
    expect(isStaleVersion(new Error('network'))).toBe(false);
  });
});
