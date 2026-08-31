import { describe, expect, it, vi } from 'vitest';
import { VistaraApiClient, VistaraApiError } from './generated/client';
import { retryAfterSeconds, withRetryAfter } from './throttling';

function problem(status: number) {
  return JSON.stringify({
    type: 'about:blank',
    title: 'rate_limited',
    status,
    code: 'rate_limited',
    errors: {},
  });
}

describe('rate limit answers', () => {
  it('keeps the delay the API asked for where the client can read it', async () => {
    const fetch = vi.fn(
      async () =>
        new Response(problem(429), {
          status: 429,
          headers: {
            'Content-Type': 'application/problem+json',
            'Retry-After': '7',
          },
        }),
    );
    const client = new VistaraApiClient({
      fetch: withRetryAfter(fetch as never),
    });

    const error = await client
      .listAssets()
      .then(() => undefined)
      .catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(VistaraApiError);
    expect(retryAfterSeconds(error)).toBe(7);
  });

  it('reads an HTTP date as a delay in seconds', async () => {
    const now = Date.now();
    const fetch = vi.fn(
      async () =>
        new Response(problem(429), {
          status: 429,
          headers: {
            'Content-Type': 'application/problem+json',
            'Retry-After': new Date(now + 5_000).toUTCString(),
          },
        }),
    );
    const client = new VistaraApiClient({
      fetch: withRetryAfter(fetch as never),
    });

    const error = await client
      .listAssets()
      .then(() => undefined)
      .catch((thrown: unknown) => thrown);

    expect(retryAfterSeconds(error)).toBeGreaterThan(0);
    expect(retryAfterSeconds(error)).toBeLessThanOrEqual(6);
  });

  it('leaves every other answer exactly as it was', async () => {
    const body = JSON.stringify({ items: [] });
    const fetch = vi.fn(
      async () =>
        new Response(body, {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"v3"' },
        }),
    );
    const client = new VistaraApiClient({
      fetch: withRetryAfter(fetch as never),
    });

    await expect(client.listAssets()).resolves.toEqual({
      data: { items: [] },
      etag: '"v3"',
    });
  });

  it('reports no delay when the answer carried none', async () => {
    const fetch = vi.fn(
      async () =>
        new Response(problem(429), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
    );
    const client = new VistaraApiClient({
      fetch: withRetryAfter(fetch as never),
    });

    const error = await client
      .listAssets()
      .then(() => undefined)
      .catch((thrown: unknown) => thrown);

    expect(retryAfterSeconds(error)).toBeUndefined();
    expect(retryAfterSeconds(new Error('other'))).toBeUndefined();
  });
});
