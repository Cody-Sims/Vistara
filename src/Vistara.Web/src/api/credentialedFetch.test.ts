import { describe, expect, it, vi } from 'vitest';
import { SessionCredentials } from './credentials';
import { credentialedFetch } from './credentialedFetch';

function jsonResponse(body: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

function antiforgeryRefusal() {
  return new Response(
    JSON.stringify({
      type: 'https://vistara.dev/problems/cookie_auth-antiforgery_required',
      title: 'A valid antiforgery token is required',
      status: 403,
      code: 'cookie_auth.antiforgery_required',
    }),
    { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

function cookieCredentials(token = 'token-1', headerName = 'X-Vistara-CSRF') {
  const credentials = new SessionCredentials();
  credentials.adopt({
    csrfHeaderName: headerName,
    csrfToken: token,
    authenticationKind: 'cookie',
  });
  return credentials;
}

function sentHeaders(init: RequestInit | undefined) {
  return new Headers(init?.headers);
}

describe('same-origin requests carrying a cookie session', () => {
  it('sends the configured antiforgery header on an unsafe request', async () => {
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, cookieCredentials('token-1', 'X-Deployment-CSRF'));

    await send('/api/v1/albums', {
      method: 'POST',
      headers: { 'Idempotency-Key': 'key-1' },
      body: '{}',
    });

    const headers = sentHeaders(inner.mock.calls[0]![1]);
    expect(headers.get('X-Deployment-CSRF')).toBe('token-1');
    expect(headers.get('Idempotency-Key')).toBe('key-1');
  });

  it('sends no antiforgery header on a safe request', async () => {
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, cookieCredentials());

    await send('/api/v1/albums');

    expect(sentHeaders(inner.mock.calls[0]![1]).get('X-Vistara-CSRF')).toBeNull();
  });

  it('sends no antiforgery header for a credential the API issues none to', async () => {
    const credentials = new SessionCredentials();
    credentials.adopt({ authenticationKind: 'apiKey' });
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, credentials);

    await send('/api/v1/albums', { method: 'POST', body: '{}' });

    expect(sentHeaders(inner.mock.calls[0]![1]).get('X-Vistara-CSRF')).toBeNull();
  });

  it('sends no antiforgery token to another origin', async () => {
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, cookieCredentials());

    await send('https://storage.invalid/part-1', {
      method: 'PUT',
      body: 'bytes',
    });

    expect(sentHeaders(inner.mock.calls[0]![1]).get('X-Vistara-CSRF')).toBeNull();
  });

  it('reads the session once when concurrent mutations start before it is known', async () => {
    const credentials = new SessionCredentials();
    const refresh = vi.fn(async () => {
      credentials.adopt({ csrfToken: 'token-9', authenticationKind: 'cookie' });
    });
    credentials.useRefresher(refresh);
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, credentials);

    await Promise.all([
      send('/api/v1/uploads', { method: 'POST', body: '{}' }),
      send('/api/v1/albums', { method: 'POST', body: '{}' }),
    ]);

    expect(refresh).toHaveBeenCalledTimes(1);
    for (const call of inner.mock.calls) {
      expect(sentHeaders(call[1]).get('X-Vistara-CSRF')).toBe('token-9');
    }
  });

  it('keeps a token the caller set instead of replacing it', async () => {
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, cookieCredentials());

    await send('/api/v1/albums', {
      method: 'POST',
      headers: { 'X-Vistara-CSRF': 'caller-token' },
      body: '{}',
    });

    const headers = sentHeaders(inner.mock.calls[0]![1]);
    expect(headers.get('X-Vistara-CSRF')).toBe('caller-token');
  });

  it('replays a refused mutation once after the session rotated its token', async () => {
    const credentials = cookieCredentials('stale');
    credentials.useRefresher(async () => {
      credentials.adopt({ csrfToken: 'rotated', authenticationKind: 'cookie' });
    });
    const inner = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(antiforgeryRefusal())
      .mockResolvedValueOnce(jsonResponse({ id: 'album-1' }, { status: 201 }));
    const send = credentialedFetch(inner, credentials);

    const response = await send('/api/v1/albums', {
      method: 'POST',
      headers: { 'Idempotency-Key': 'key-1' },
      body: '{"name":"Summer"}',
    });

    expect(response.status).toBe(201);
    expect(inner).toHaveBeenCalledTimes(2);
    const replayed = inner.mock.calls[1]!;
    expect(sentHeaders(replayed[1]).get('X-Vistara-CSRF')).toBe('rotated');
    expect(sentHeaders(replayed[1]).get('Idempotency-Key')).toBe('key-1');
    expect(replayed[1]?.body).toBe('{"name":"Summer"}');
  });

  it('replays nothing when the refused mutation was not refused for antiforgery', async () => {
    const credentials = cookieCredentials('token-1');
    credentials.useRefresher(async () => {
      credentials.adopt({ csrfToken: 'token-2', authenticationKind: 'cookie' });
    });
    const forbidden = new Response(
      JSON.stringify({ status: 403, code: 'auth.forbidden' }),
      { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
    );
    const inner = vi.fn<typeof globalThis.fetch>(async () => forbidden.clone());
    const send = credentialedFetch(inner, credentials);

    const response = await send('/api/v1/albums', { method: 'POST', body: '{}' });

    expect(inner).toHaveBeenCalledTimes(1);
    expect(response.status).toBe(403);
    expect(await response.json()).toMatchObject({ code: 'auth.forbidden' });
  });

  it('replays nothing when the session answers the same token again', async () => {
    const credentials = cookieCredentials('token-1');
    credentials.useRefresher(async () => undefined);
    const inner = vi.fn<typeof globalThis.fetch>(async () => antiforgeryRefusal());
    const send = credentialedFetch(inner, credentials);

    const response = await send('/api/v1/albums', { method: 'POST', body: '{}' });

    expect(inner).toHaveBeenCalledTimes(1);
    expect(response.status).toBe(403);
  });

  it('replays no mutation whose body cannot be read again', async () => {
    const credentials = cookieCredentials('stale');
    credentials.useRefresher(async () => {
      credentials.adopt({ csrfToken: 'rotated', authenticationKind: 'cookie' });
    });
    const inner = vi.fn<typeof globalThis.fetch>(async () => antiforgeryRefusal());
    const send = credentialedFetch(inner, credentials);

    const response = await send('/api/v1/uploads', {
      method: 'POST',
      body: new ReadableStream(),
      // @ts-expect-error duplex is required for a streamed body at runtime.
      duplex: 'half',
    });

    expect(inner).toHaveBeenCalledTimes(1);
    expect(response.status).toBe(403);
  });

  it('leaves the refused response readable when nothing is replayed', async () => {
    const credentials = cookieCredentials('token-1');
    credentials.useRefresher(async () => undefined);
    const inner = vi.fn<typeof globalThis.fetch>(async () => antiforgeryRefusal());
    const send = credentialedFetch(inner, credentials);

    const response = await send('/api/v1/albums', { method: 'POST', body: '{}' });

    expect(await response.json()).toMatchObject({
      code: 'cookie_auth.antiforgery_required',
    });
  });

  it('reads no session for an anonymous route', async () => {
    const credentials = new SessionCredentials();
    const refresh = vi.fn(async () => undefined);
    credentials.useRefresher(refresh);
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    const send = credentialedFetch(inner, credentials);

    await send('/api/v1/public/shares/token-1/challenge', {
      method: 'POST',
      body: '{}',
    });

    expect(refresh).not.toHaveBeenCalled();
  });

  it('reads the global fetch at call time when none is supplied', async () => {
    const inner = vi.fn<typeof globalThis.fetch>(async () => jsonResponse({}));
    vi.stubGlobal('fetch', inner);
    const send = credentialedFetch(undefined, cookieCredentials());

    await send('/api/v1/albums', { method: 'POST', body: '{}' });

    expect(sentHeaders(inner.mock.calls[0]![1]).get('X-Vistara-CSRF')).toBe(
      'token-1',
    );
    vi.unstubAllGlobals();
  });
});

describe('a request object refused for antiforgery', () => {
  /** A request object needs an absolute URL; this page is the API's origin. */
  function apiRequest(path: string, init: RequestInit = {}) {
    return new Request(`${globalThis.location.origin}${path}`, init);
  }

  function rotating(token: string) {
    const credentials = cookieCredentials(token);
    credentials.useRefresher(async () => {
      credentials.adopt({ csrfToken: 'rotated', authenticationKind: 'cookie' });
    });
    return credentials;
  }

  it('is sent again with the body the first attempt consumed', async () => {
    const credentials = rotating('stale');
    const inner = vi
      .fn<typeof globalThis.fetch>()
      .mockImplementationOnce(async (input, init) => {
        await new Request(input as RequestInfo, init).text();
        return antiforgeryRefusal();
      })
      .mockResolvedValueOnce(jsonResponse({ id: 'album-1' }, { status: 201 }));
    const send = credentialedFetch(inner, credentials);

    const response = await send(
      apiRequest('/api/v1/albums', {
        method: 'POST',
        headers: {
          'Idempotency-Key': 'key-1',
          'Content-Type': 'application/json',
        },
        body: '{"name":"Summer"}',
      }),
    );

    expect(response.status).toBe(201);
    expect(inner).toHaveBeenCalledTimes(2);
    const [replayed, replayedInit] = inner.mock.calls[1]!;
    expect(replayed).toBeInstanceOf(Request);
    const sent = new Request(replayed as RequestInfo, replayedInit);
    expect(sent.method).toBe('POST');
    expect(await sent.text()).toBe('{"name":"Summer"}');
  });

  it('keeps the headers the caller set and the rotated token', async () => {
    const credentials = rotating('stale');
    const inner = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(antiforgeryRefusal())
      .mockResolvedValueOnce(jsonResponse({}, { status: 201 }));
    const send = credentialedFetch(inner, credentials);

    await send(
      apiRequest('/api/v1/albums', {
        method: 'POST',
        headers: {
          'Idempotency-Key': 'key-1',
          'Content-Type': 'application/json',
        },
        body: '{}',
      }),
    );

    const headers = sentHeaders(inner.mock.calls[1]![1]);
    expect(headers.get('Idempotency-Key')).toBe('key-1');
    expect(headers.get('Content-Type')).toBe('application/json');
    expect(headers.get('X-Vistara-CSRF')).toBe('rotated');
  });

  it('is never sent again once its body has been read', async () => {
    const credentials = rotating('stale');
    const inner = vi.fn<typeof globalThis.fetch>(async () =>
      antiforgeryRefusal(),
    );
    const send = credentialedFetch(inner, credentials);
    const request = apiRequest('/api/v1/albums', {
      method: 'POST',
      body: '{"name":"Summer"}',
    });
    await request.text();
    expect(request.bodyUsed).toBe(true);

    const response = await send(request);

    expect(inner).toHaveBeenCalledTimes(1);
    expect(response.status).toBe(403);
  });

  it('is never sent again when its body is a stream that cannot be duplicated', async () => {
    const credentials = rotating('stale');
    const inner = vi.fn<typeof globalThis.fetch>(async () =>
      antiforgeryRefusal(),
    );
    const send = credentialedFetch(inner, credentials);

    const response = await send('/api/v1/uploads', {
      method: 'POST',
      body: new ReadableStream(),
      // @ts-expect-error duplex is required for a streamed body at runtime.
      duplex: 'half',
    });

    expect(inner).toHaveBeenCalledTimes(1);
    expect(response.status).toBe(403);
  });

  it('carries the abort signal of the caller into the second attempt', async () => {
    const credentials = rotating('stale');
    const controller = new AbortController();
    const inner = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(antiforgeryRefusal())
      .mockResolvedValueOnce(jsonResponse({}, { status: 201 }));
    const send = credentialedFetch(inner, credentials);

    await send('/api/v1/albums', {
      method: 'POST',
      body: '{}',
      signal: controller.signal,
    });

    expect(inner.mock.calls[1]![1]?.signal).toBe(controller.signal);
  });

  it('is cancelled rather than replayed when the caller aborts', async () => {
    const credentials = cookieCredentials('stale');
    const controller = new AbortController();
    credentials.useRefresher(async () => {
      controller.abort();
      credentials.adopt({ csrfToken: 'rotated', authenticationKind: 'cookie' });
    });
    const inner = vi.fn<typeof globalThis.fetch>(async (_input, init) => {
      if (init?.signal?.aborted) {
        throw new DOMException('Cancelled', 'AbortError');
      }

      return antiforgeryRefusal();
    });
    const send = credentialedFetch(inner, credentials);

    await expect(
      send(
        apiRequest('/api/v1/albums', { method: 'POST', body: '{}' }),
        { signal: controller.signal },
      ),
    ).rejects.toMatchObject({ name: 'AbortError' });
    expect(inner).toHaveBeenCalledTimes(2);
  });

  it('is attempted exactly once more, however often it is refused', async () => {
    const credentials = cookieCredentials('token-0');
    let issued = 0;
    credentials.useRefresher(async () => {
      issued += 1;
      credentials.adopt({
        csrfToken: `token-${issued}`,
        authenticationKind: 'cookie',
      });
    });
    const inner = vi.fn<typeof globalThis.fetch>(async () =>
      antiforgeryRefusal(),
    );
    const send = credentialedFetch(inner, credentials);

    const response = await send(
      apiRequest('/api/v1/albums', { method: 'POST', body: '{}' }),
    );

    expect(inner).toHaveBeenCalledTimes(2);
    expect(response.status).toBe(403);
    expect(await response.json()).toMatchObject({
      code: 'cookie_auth.antiforgery_required',
    });
  });
});
