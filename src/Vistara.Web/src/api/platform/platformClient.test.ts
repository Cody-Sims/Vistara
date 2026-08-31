import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../generated/client';
import { isStaleVersion, isStateConflict, versionTag } from '../versionTag';
import { describeRetryAfter, VistaraThrottledError } from './throttling';
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
  csrfToken: 'session-token',
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
      // The ended session answers the next bootstrap with 401.
      .mockResolvedValueOnce(problemResponse(401, 'auth.unauthenticated'))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.login({ login: 'ada@example.test', password: 'pw' });
    await client.logout();
    await client.logout();

    expect(fetch.mock.calls[2]![0]).toBe('/api/v1/me');
    expect(
      new Headers(fetch.mock.calls[3]![1]?.headers).get('X-Vistara-CSRF'),
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
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse({ items: [] }))
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockImplementation(async () => jsonResponse({ items: [] }));
    const client = new PlatformApiClient({ fetch });

    await client.listApiKeys();
    await client.createApiKey({ scopes: ['assets.read'] });
    await client.revokeApiKey('key 1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/api-keys');
    expect(fetch.mock.calls[1]![0]).toBe('/api/v1/me');
    expect(fetch.mock.calls[2]![1]?.method).toBe('POST');
    expect(fetch.mock.calls[3]![0]).toBe('/api/v1/api-keys/key%201');
    expect(fetch.mock.calls[3]![1]?.method).toBe('DELETE');
  });

  it('reads a single job by identifier', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({
        id: 'job-1',
        type: 'derivatives',
        state: 'deadLettered',
        attempts: 3,
        maxAttempts: 5,
        createdAt: '2026-02-01T00:00:00Z',
        availableAt: '2026-02-01T00:05:00Z',
        version: 4,
        actions: { retry: true, cancel: false },
      }),
    );
    const client = new PlatformApiClient({ fetch });

    const job = await client.getJob('job/1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/jobs/job%2F1');
    expect(job.state).toBe('deadLettered');
    expect(job.actions).toEqual({ retry: true, cancel: false });
  });

  it('exposes only the routes the API branch publishes', () => {
    const client = new PlatformApiClient();
    const methods = Object.getOwnPropertyNames(
      Object.getPrototypeOf(client) as object,
    ).filter((name) => name !== 'constructor');

    expect(methods.sort()).toEqual(
      [
        'antiforgeryHeaderName',
        'cancelJob',
        'createApiKey',
        'getCapabilities',
        'getJob',
        'getPolicies',
        'getPreferences',
        'getSession',
        'getSetupState',
        'getStorageSummary',
        'getStorageValidationSupport',
        'provisionFirstOwner',
        'validateStorage',
        'inviteTenantMember',
        'listApiKeys',
        'listJobs',
        'listTenantMembers',
        'listTenants',
        'login',
        'logout',
        'onUnauthorized',
        'retryJob',
        'revokeApiKey',
        'updatePreferences',
        'updateTenantMember',
      ].sort(),
    );
  });
});

describe('restored browser sessions', () => {
  it('adopts the antiforgery token published for the cookie session', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.getSession();
    await client.logout();

    expect(
      new Headers(fetch.mock.calls[1]![1]?.headers).get('X-Vistara-CSRF'),
    ).toBe('session-token');
  });

  it('reads the session once before the first unsafe request after a reload', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new PlatformApiClient({ fetch });

    await client.revokeApiKey('key-1');

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/me');
    expect(fetch.mock.calls[1]![0]).toBe('/api/v1/api-keys/key-1');
    expect(
      new Headers(fetch.mock.calls[1]![1]?.headers).get('X-Vistara-CSRF'),
    ).toBe('session-token');
  });

  it('never delays sign-in behind a session read', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ user: currentUser, csrfToken: 'login-token' }),
    );
    const client = new PlatformApiClient({ fetch });

    await client.login({ login: 'ada@example.test', password: 'pw' });

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/auth/login');
  });
});

describe('versioned platform edits', () => {
  it('sends If-Match and returns the new entity tag for preferences', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(
        jsonResponse(
          {
            density: 'compact',
            reducedMotion: true,
            screenReaderPagedMode: false,
            version: 4,
          },
          { headers: { ETag: '"v4"', 'Content-Type': 'application/json' } },
        ),
      );
    const client = new PlatformApiClient({ fetch });

    const updated = await client.updatePreferences(
      { density: 'compact' },
      { ifMatch: versionTag(3) },
    );

    const [url, init] = fetch.mock.calls[1]!;
    expect(url).toBe('/api/v1/me/preferences');
    expect(init?.method).toBe('PATCH');
    expect(new Headers(init?.headers).get('If-Match')).toBe('"v3"');
    expect(updated.etag).toBe('"v4"');
    expect(updated.data.density).toBe('compact');
  });

  it('changes a member with the member route and its version', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(
        jsonResponse(
          { userId: 'user-2', role: 'TenantAdmin', version: 5 },
          { headers: { ETag: '"v5"', 'Content-Type': 'application/json' } },
        ),
      );
    const client = new PlatformApiClient({ fetch });

    await client.updateTenantMember(
      'tenant-a',
      'user 2',
      { role: 'TenantAdmin' },
      { ifMatch: versionTag(4) },
    );

    const [url, init] = fetch.mock.calls[1]!;
    expect(url).toBe('/api/v1/tenants/tenant-a/members/user%202');
    expect(new Headers(init?.headers).get('If-Match')).toBe('"v4"');
  });

  it('lists and acts on jobs through the job routes', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse({ items: [] }))
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(jsonResponse({ id: 'job-1', version: 3 }));
    const client = new PlatformApiClient({ fetch });

    await client.listJobs({
      states: ['deadLettered', 'retryScheduled'],
      limit: 25,
    });
    await client.retryJob('job-1', { ifMatch: versionTag(2) });

    expect(fetch.mock.calls[0]![0]).toBe(
      '/api/v1/jobs?states=deadLettered&states=retryScheduled&limit=25',
    );
    expect(fetch.mock.calls[2]![0]).toBe('/api/v1/jobs/job-1/retry');
    expect(
      new Headers(fetch.mock.calls[2]![1]?.headers).get('If-Match'),
    ).toBe('"v2"');
  });
});

describe('first-run provisioning client', () => {
  it('reads whether setup is still open', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ available: true }),
    );
    const client = new PlatformApiClient({ fetch });

    const state = await client.getSetupState();

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/setup');
    expect(fetch.mock.calls[0]![1]?.method).toBe('GET');
    expect(state.available).toBe(true);
  });

  it('provisions the first owner without a session read first', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse(
        {
          tenantId: 'tenant-a',
          tenantSlug: 'studio',
          tenantName: 'Studio',
          userId: 'user-1',
          email: 'ada@example.test',
          displayName: 'Ada Lovelace',
          role: 'TenantOwner',
        },
        { status: 201 },
      ),
    );
    const client = new PlatformApiClient({ fetch });

    const owner = await client.provisionFirstOwner({
      tenantSlug: 'studio',
      tenantName: 'Studio',
      email: 'ada@example.test',
      displayName: 'Ada Lovelace',
      password: 'a very long owner password',
    });

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/setup');
    expect(owner.role).toBe('TenantOwner');
  });

  it('reports an already provisioned platform as a conflict', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      problemResponse(409, 'setup.already_provisioned'),
    );
    const client = new PlatformApiClient({ fetch });

    const error = await client
      .provisionFirstOwner({
        tenantSlug: 'studio',
        tenantName: 'Studio',
        email: 'ada@example.test',
        displayName: 'Ada Lovelace',
        password: 'a very long owner password',
      })
      .catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(VistaraApiError);
    expect((error as VistaraApiError).problem.code).toBe(
      'setup.already_provisioned',
    );
  });
});

describe('storage administration client', () => {
  it('reads the published consumption summary', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({
        buckets: [],
        originalBytes: 1,
        derivativeBytes: 2,
        stagingBytes: 3,
        quotaBytes: 4,
        pendingUploadBytes: 5,
      }),
    );
    const client = new PlatformApiClient({ fetch });

    const summary = await client.getStorageSummary();

    expect(fetch.mock.calls[0]![0]).toBe('/api/v1/admin/storage');
    expect(summary.pendingUploadBytes).toBe(5);
  });

  it('sends a candidate configuration for validation and keeps nothing', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(
        jsonResponse({
          valid: true,
          provider: 's3',
          checks: [{ id: 'write', status: 'passed' }],
        }),
      );
    const client = new PlatformApiClient({ fetch });

    const result = await client.validateStorage({
      provider: 's3',
      s3: {
        endpoint: 'https://s3.example',
        region: 'eu-central-1',
        bucket: 'vistara-media',
        accessKeyId: 'AKIAEXAMPLE',
        secretAccessKey: 'super-secret-value',
        forcePathStyle: true,
      },
    });

    const [url, init] = fetch.mock.calls[1]!;
    expect(url).toBe('/api/v1/admin/storage/validate');
    expect(init?.method).toBe('POST');
    expect(String(init?.body)).toContain('super-secret-value');
    expect(result.valid).toBe(true);

    // The credential travelled once. Every later request is observed for it:
    // no body, no header, and no query string may carry it again.
    fetch.mockImplementation(async () => jsonResponse({ items: [] }));
    await client.getStorageSummary();
    await client.getStorageValidationSupport();
    await client.listApiKeys();
    await client.validateStorage({
      provider: 'filesystem',
      filesystem: { rootPath: '/srv/vistara/media' },
    });

    const laterCalls = fetch.mock.calls.slice(2);
    expect(laterCalls.length).toBeGreaterThan(3);
    for (const [target, request] of laterCalls) {
      expect(String(target)).not.toContain('super-secret-value');
      expect(String(request?.body ?? '')).not.toContain('super-secret-value');
      for (const [, value] of new Headers(request?.headers)) {
        expect(value).not.toContain('super-secret-value');
      }
    }
  });

  it('replays nothing from a rejected validation', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(problemResponse(422, 'storage_validation.invalid_request'))
      .mockImplementation(async () =>
        jsonResponse({ supported: true, providers: ['s3'] }),
      );
    const client = new PlatformApiClient({ fetch });

    await client
      .validateStorage({
        provider: 'azureBlob',
        azureBlob: {
          accountName: 'vistaramedia',
          container: 'originals',
          credentialKind: 'accountKey',
          accountKey: 'rejected-account-key',
        },
      })
      .catch(() => undefined);
    await client.getStorageValidationSupport();

    const afterFailure = fetch.mock.calls.slice(2);
    expect(afterFailure).not.toHaveLength(0);
    for (const [target, request] of afterFailure) {
      expect(String(target)).not.toContain('rejected-account-key');
      expect(String(request?.body ?? '')).not.toContain('rejected-account-key');
      for (const [, value] of new Headers(request?.headers)) {
        expect(value).not.toContain('rejected-account-key');
      }
    }
  });
});

describe('throttling and cancellation', () => {
  it('reads Retry-After from a throttled setup probe', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      new Response(
        JSON.stringify({
          type: 'about:blank',
          title: 'setup.throttled',
          status: 429,
          code: 'setup.throttled',
          errors: {},
        }),
        {
          status: 429,
          headers: {
            'Content-Type': 'application/problem+json',
            'Retry-After': '30',
          },
        },
      ),
    );
    const client = new PlatformApiClient({ fetch });

    const error = await client
      .getSetupState()
      .catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(VistaraThrottledError);
    expect((error as VistaraThrottledError).retryAfterSeconds).toBe(30);
    expect((error as VistaraThrottledError).problem.code).toBe(
      'setup.throttled',
    );
  });

  it('describes a wait for a screen reader', () => {
    expect(describeRetryAfter(undefined)).toContain('Wait a moment');
    expect(describeRetryAfter(1)).toContain('a second');
    expect(describeRetryAfter(30)).toContain('30 seconds');
    expect(describeRetryAfter(240)).toContain('4 minutes');
  });

  it('passes an abort signal through to the validation request', async () => {
    const controller = new AbortController();
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      jsonResponse({ supported: true, providers: ['s3'] }),
    );
    const client = new PlatformApiClient({ fetch });

    await client.validateStorage(
      { provider: 'filesystem', filesystem: { rootPath: '/srv/media' } },
      { signal: controller.signal },
    );

    expect(fetch.mock.calls.at(-1)?.[1]?.signal).toBe(controller.signal);
  });
});

describe('candidate storage credentials', () => {
  it('sends an Azure managed identity without any secret member', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(
        jsonResponse({ valid: true, provider: 'azureBlob', checks: [] }),
      );
    const client = new PlatformApiClient({ fetch });

    await client.validateStorage({
      provider: 'azureBlob',
      azureBlob: {
        accountName: 'vistaramedia',
        container: 'originals',
        credentialKind: 'managedIdentity',
      },
    });

    const body = JSON.parse(String(fetch.mock.calls[1]![1]?.body)) as {
      azureBlob: Record<string, unknown>;
    };
    expect(body.azureBlob.credentialKind).toBe('managedIdentity');
    expect(body.azureBlob).not.toHaveProperty('accountKey');
    expect(body.azureBlob).not.toHaveProperty('sasToken');
  });

  it('sends an S3 session token alongside the access key', async () => {
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(jsonResponse(currentUser))
      .mockResolvedValueOnce(
        jsonResponse({ valid: true, provider: 's3', checks: [] }),
      );
    const client = new PlatformApiClient({ fetch });

    await client.validateStorage({
      provider: 's3',
      s3: {
        endpoint: 'https://s3.example',
        region: 'eu-central-1',
        bucket: 'vistara-media',
        forcePathStyle: true,
        accessKeyId: 'AKIAEXAMPLE',
        secretAccessKey: 'secret-value',
        sessionToken: 'session-value',
      },
    });

    const body = JSON.parse(String(fetch.mock.calls[1]![1]?.body)) as {
      s3: Record<string, unknown>;
    };
    expect(body.s3.sessionToken).toBe('session-value');
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
