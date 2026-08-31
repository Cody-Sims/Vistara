import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { sessionCredentials } from '../api/credentials';
import { FrozenUploadClient } from '../features/uploads';
import { galleryClient, platformClient } from './apiClients';

/**
 * The deployment the browser talks to: a cookie session that refuses every
 * unsafe request arriving without the antiforgery header it published, exactly
 * as the platform antiforgery middleware does.
 */
class CookieSessionApi {
  public token = 'token-1';
  public readonly headerName = 'X-Vistara-CSRF';
  public readonly sessionReads: string[] = [];
  public readonly refused: string[] = [];
  public readonly accepted: string[] = [];

  public readonly fetch = vi.fn<typeof globalThis.fetch>(
    async (input, init) => {
      const url = new URL(String(input), 'https://vistara.test');
      const method = (init?.method ?? 'GET').toUpperCase();
      const headers = new Headers(init?.headers);
      if (url.pathname === '/api/v1/me') {
        this.sessionReads.push(this.token);
        return this.json(this.session());
      }

      if (method === 'GET' || method === 'HEAD') {
        return this.json({ items: [] });
      }

      if (headers.get(this.headerName) !== this.token) {
        this.refused.push(`${method} ${url.pathname}`);
        return this.problem();
      }

      this.accepted.push(`${method} ${url.pathname}`);
      return this.json({ id: 'created', uploadId: 'upload-1', version: 1 }, 201);
    },
  );

  public session() {
    return {
      userId: 'user-1',
      email: 'ada@example.test',
      displayName: 'Ada Lovelace',
      tenantId: 'tenant-a',
      role: 'TenantOwner',
      tenants: [],
      authenticationKind: 'cookie',
      csrfHeaderName: this.headerName,
      csrfToken: this.token,
    };
  }

  private json(body: unknown, status = 200) {
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  private problem() {
    return new Response(
      JSON.stringify({
        type: 'https://vistara.dev/problems/cookie_auth-antiforgery_required',
        title: 'A valid antiforgery token is required',
        status: 403,
        code: 'cookie_auth.antiforgery_required',
      }),
      {
        status: 403,
        headers: { 'Content-Type': 'application/problem+json' },
      },
    );
  }
}

class FakeXhr {
  public upload: { onprogress: ((event: ProgressEvent) => void) | null } = {
    onprogress: null,
  };
  public onload: (() => void) | null = null;
  public onerror: (() => void) | null = null;
  public onabort: (() => void) | null = null;
  public status = 200;
  public withCredentials = false;
  public readonly headers = new Map<string, string>();

  public open() {}

  public setRequestHeader(name: string, value: string) {
    this.headers.set(name, value);
  }

  public getResponseHeader(name: string) {
    return name === 'ETag' ? '"v2"' : null;
  }

  public send() {
    this.onload?.();
  }

  public abort() {
    this.onabort?.();
  }
}

let api: CookieSessionApi;

beforeEach(() => {
  api = new CookieSessionApi();
  vi.stubGlobal('fetch', api.fetch);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('cookie session mutations from the gallery and upload clients', () => {
  it('creates an album with the antiforgery header the session published', async () => {
    await platformClient.getSession();

    const created = await galleryClient.createAlbum(
      { name: 'Summer' },
      { idempotencyKey: 'album-key-1' },
    );

    expect(created.data).toMatchObject({ id: 'created' });
    expect(api.refused).toHaveLength(0);
    expect(api.accepted).toContain('POST /api/v1/albums');
  });

  it('creates an upload with the antiforgery header the session published', async () => {
    await platformClient.getSession();
    const client = new FrozenUploadClient();

    const status = await client.create(
      new File(['bytes'], 'tiny.png', { type: 'image/png' }),
      'a'.repeat(64),
      'upload-key-1',
    );

    expect(status).toMatchObject({ uploadId: 'upload-1' });
    expect(api.refused).toHaveLength(0);
    expect(api.accepted).toContain('POST /api/v1/uploads');
  });

  it('reads the session once when a reloaded browser mutates before it knows the token', async () => {
    const [album, upload] = await Promise.all([
      galleryClient.createAlbum({ name: 'Autumn' }, { idempotencyKey: 'k-1' }),
      new FrozenUploadClient().create(
        new File(['bytes'], 'tiny.png', { type: 'image/png' }),
        'b'.repeat(64),
        'k-2',
      ),
    ]);

    expect(album.data).toMatchObject({ id: 'created' });
    expect(upload).toMatchObject({ uploadId: 'upload-1' });
    expect(api.sessionReads).toHaveLength(1);
    expect(api.refused).toHaveLength(0);
  });

  it('sends the antiforgery header with a proxied upload the browser streams', async () => {
    await platformClient.getSession();
    const xhr = new FakeXhr();
    const client = new FrozenUploadClient({
      xhr: () => xhr as unknown as XMLHttpRequest,
    });

    await client.proxy(
      '/api/v1/uploads/upload-1/content',
      1,
      new File(['bytes'], 'tiny.png', { type: 'image/png' }),
      () => {},
      new AbortController().signal,
    );

    expect(xhr.headers.get('X-Vistara-CSRF')).toBe('token-1');
    expect(xhr.withCredentials).toBe(true);
  });

  it('sends no antiforgery header to the storage service that signed the part', async () => {
    await platformClient.getSession();
    const xhr = new FakeXhr();
    const client = new FrozenUploadClient({
      xhr: () => xhr as unknown as XMLHttpRequest,
    });

    await client.signed(
      {
        method: 'PUT',
        url: 'https://storage.invalid/private-token',
        headers: { 'x-signed': 'grant' },
      },
      new Blob(['bytes']),
      () => {},
      new AbortController().signal,
    );

    expect(xhr.headers.get('X-Vistara-CSRF')).toBeUndefined();
    expect(xhr.headers.get('x-signed')).toBe('grant');
  });

  it('spends the rotated token after the session issues a new one', async () => {
    await platformClient.getSession();
    api.token = 'token-2';

    const created = await galleryClient.createAlbum(
      { name: 'Winter' },
      { idempotencyKey: 'album-key-2' },
    );

    expect(created.data).toMatchObject({ id: 'created' });
    expect(api.refused).toEqual(['POST /api/v1/albums']);
    expect(api.accepted).toEqual(['POST /api/v1/albums']);
    expect(api.sessionReads).toHaveLength(2);
  });

  it('holds no token for the next account after signing out', async () => {
    await platformClient.getSession();
    await platformClient.logout();

    expect(sessionCredentials.carriesToken).toBe(false);
  });
});
