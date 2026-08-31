import { runInNewContext } from 'node:vm';
import { describe, expect, it, vi } from 'vitest';
import { buildServiceWorker, isCacheableAsset } from './serviceWorker';

interface FakeCache {
  readonly entries: Map<string, string>;
  addAll(paths: string[]): Promise<void>;
  match(path: string): Promise<string | undefined>;
}

function createHarness(source: string, origin = 'https://gallery.example') {
  const caches = new Map<string, FakeCache>();
  const listeners = new Map<string, (event: unknown) => void>();
  const deleted: string[] = [];
  const networkResponses = new Map<string, string>();
  const fetchCalls: string[] = [];
  let networkFails = false;

  function cacheFor(name: string): FakeCache {
    const existing = caches.get(name);
    if (existing) {
      return existing;
    }

    const entries = new Map<string, string>();
    const cache: FakeCache = {
      entries,
      addAll: async (paths) => {
        for (const path of paths) {
          entries.set(path, `cached:${path}`);
        }
      },
      match: async (path) => entries.get(path),
    };
    caches.set(name, cache);
    return cache;
  }

  const sandbox = {
    self: {
      addEventListener: (type: string, listener: (event: unknown) => void) => {
        listeners.set(type, listener);
      },
      skipWaiting: vi.fn(async () => undefined),
      clients: { claim: vi.fn(async () => undefined) },
      location: { origin },
    },
    caches: {
      open: async (name: string) => cacheFor(name),
      keys: async () => [...caches.keys()],
      delete: async (name: string) => {
        deleted.push(name);
        return caches.delete(name);
      },
    },
    fetch: async (request: { url: string }) => {
      fetchCalls.push(request.url);
      if (networkFails) {
        throw new Error('offline');
      }

      return networkResponses.get(request.url) ?? `network:${request.url}`;
    },
    URL,
    Set,
    Promise,
    Response: class {
      public constructor(
        public body: string,
        public init: { status: number },
      ) {}
    },
    console,
  };

  runInNewContext(source, sandbox);

  async function dispatch(
    type: 'install' | 'activate',
  ): Promise<void> {
    const waits: Promise<unknown>[] = [];
    listeners.get(type)?.({
      waitUntil: (value: Promise<unknown>) => waits.push(value),
    });
    await Promise.all(waits);
  }

  async function request(
    url: string,
    init: { method?: string; mode?: string } = {},
  ): Promise<{ handled: boolean; response?: unknown }> {
    let response: Promise<unknown> | undefined;
    listeners.get('fetch')?.({
      request: { url, method: init.method ?? 'GET', mode: init.mode ?? 'cors' },
      respondWith: (value: Promise<unknown>) => {
        response = value;
      },
    });

    return response === undefined
      ? { handled: false }
      : { handled: true, response: await response };
  }

  return {
    caches,
    deleted,
    dispatch,
    fetchCalls,
    request,
    seedCache: (name: string, path: string, value: string) => {
      cacheFor(name).entries.set(path, value);
    },
    setOffline: (value: boolean) => {
      networkFails = value;
    },
  };
}

const source = buildServiceWorker({
  cacheName: 'vistara-shell-abc123',
  shell: '/index.html',
  precache: [
    '/index.html',
    '/assets/index-abc123.js',
    '/assets/index-abc123.css',
    '/favicon.svg',
    '/icon-192.png',
    '/manifest.webmanifest',
  ],
});

describe('cache allowlist', () => {
  it('accepts versioned shell and brand assets', () => {
    expect(isCacheableAsset('/assets/index-abc123.js')).toBe(true);
    expect(isCacheableAsset('/assets/index-abc123.css')).toBe(true);
    expect(isCacheableAsset('/favicon.svg')).toBe(true);
    expect(isCacheableAsset('/icon-512.png')).toBe(true);
    expect(isCacheableAsset('/favicon.ico')).toBe(true);
    expect(isCacheableAsset('/manifest.webmanifest')).toBe(true);
    expect(isCacheableAsset('/index.html')).toBe(true);
  });

  it('refuses anything that can carry account data', () => {
    expect(isCacheableAsset('/api/v1/me')).toBe(false);
    expect(isCacheableAsset('/api/v1/assets/1/original')).toBe(false);
    expect(isCacheableAsset('/media/thumb/abc.webp')).toBe(false);
    expect(isCacheableAsset('/delivery/grant/preview.webp')).toBe(false);
    expect(isCacheableAsset('/s/share-token')).toBe(false);
    expect(isCacheableAsset('/health')).toBe(false);
    expect(isCacheableAsset('/library')).toBe(false);
    expect(isCacheableAsset('../secret.js')).toBe(false);
  });

  it('drops uncacheable entries from a generated precache list', () => {
    const generated = buildServiceWorker({
      cacheName: 'vistara-shell-x',
      shell: '/index.html',
      precache: ['/assets/a.js', '/api/v1/me', '/media/a.webp', '/s/token'],
    });

    expect(generated).toContain('/assets/a.js');
    expect(generated).not.toContain('/api/v1/me');
    expect(generated).not.toContain('/media/a.webp');
    expect(generated).not.toContain('/s/token');
  });
});

describe('generated service worker', () => {
  it('precaches exactly the allowlisted assets on install', async () => {
    const harness = createHarness(source);

    await harness.dispatch('install');

    expect([...harness.caches.keys()]).toEqual(['vistara-shell-abc123']);
    expect([
      ...harness.caches.get('vistara-shell-abc123')!.entries.keys(),
    ].sort()).toEqual([
      '/assets/index-abc123.css',
      '/assets/index-abc123.js',
      '/favicon.svg',
      '/icon-192.png',
      '/index.html',
      '/manifest.webmanifest',
    ]);
  });

  it('removes caches from earlier builds and keeps unrelated ones', async () => {
    const harness = createHarness(source);
    harness.seedCache('vistara-shell-old', '/assets/old.js', 'stale');
    harness.seedCache('another-app', '/thing', 'keep');

    await harness.dispatch('install');
    await harness.dispatch('activate');

    expect(harness.deleted).toEqual(['vistara-shell-old']);
    expect([...harness.caches.keys()].sort()).toEqual([
      'another-app',
      'vistara-shell-abc123',
    ]);
  });

  it('serves a precached asset from the cache', async () => {
    const harness = createHarness(source);
    await harness.dispatch('install');

    const result = await harness.request(
      'https://gallery.example/assets/index-abc123.js',
    );

    expect(result.handled).toBe(true);
    expect(result.response).toBe('cached:/assets/index-abc123.js');
    expect(harness.fetchCalls).toEqual([]);
  });

  it('never handles API, media, or share requests', async () => {
    const harness = createHarness(source);
    await harness.dispatch('install');

    for (const url of [
      'https://gallery.example/api/v1/me',
      'https://gallery.example/api/v1/assets',
      'https://gallery.example/media/thumb/abc.webp',
      'https://gallery.example/delivery/grant/preview.webp',
      'https://gallery.example/s/share-token',
    ]) {
      expect((await harness.request(url)).handled).toBe(false);
    }

    expect(
      [...harness.caches.get('vistara-shell-abc123')!.entries.keys()],
    ).not.toContain('/api/v1/me');
  });

  it('ignores unsafe methods and other origins', async () => {
    const harness = createHarness(source);
    await harness.dispatch('install');

    expect(
      (
        await harness.request('https://gallery.example/api/v1/auth/login', {
          method: 'POST',
        })
      ).handled,
    ).toBe(false);
    expect(
      (await harness.request('https://images.example/assets/index-abc123.js'))
        .handled,
    ).toBe(false);
  });

  it('answers a navigation from the network first', async () => {
    const harness = createHarness(source);
    await harness.dispatch('install');

    const result = await harness.request('https://gallery.example/library', {
      mode: 'navigate',
    });

    expect(result.handled).toBe(true);
    expect(result.response).toBe('network:https://gallery.example/library');
  });

  it('falls back to the cached shell when the network is unavailable', async () => {
    const harness = createHarness(source);
    await harness.dispatch('install');
    harness.setOffline(true);

    const result = await harness.request('https://gallery.example/library', {
      mode: 'navigate',
    });

    expect(result.response).toBe('cached:/index.html');
  });
});
