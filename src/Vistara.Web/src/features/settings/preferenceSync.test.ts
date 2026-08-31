import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type {
  UpdateUserPreferencesRequest,
  UserPreferences,
} from '../../api/platform';
import type { AppPreferences } from '../../app/preferences';
import { PreferenceSync, type PreferenceGateway } from './preferenceSync';

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'Failed',
    status,
    code: 'failed',
    errors: {},
  });
}

const stored: UserPreferences = {
  density: 'comfortable',
  reducedMotion: false,
  screenReaderPagedMode: false,
  version: 3,
};

/** The three fields of a patch the device and the stored document share. */
function deviceOnly(patch: UpdateUserPreferencesRequest) {
  return {
    ...(patch.density ? { density: patch.density } : {}),
    ...(patch.reducedMotion === undefined
      ? {}
      : { reducedMotion: patch.reducedMotion }),
    ...(patch.screenReaderPagedMode === undefined
      ? {}
      : { screenReaderPagedMode: patch.screenReaderPagedMode }),
  };
}

/** Records what the device was asked to apply, newest state last. */
function device() {
  let value: Partial<AppPreferences> = {};
  const seen: Partial<AppPreferences>[] = [];

  return {
    apply(update: Partial<AppPreferences>) {
      value = { ...value, ...update };
      seen.push(value);
    },
    get current() {
      return value;
    },
    get history() {
      return seen;
    },
  };
}

/**
 * A server that answers `If-Match` the way the API does: the document moves on
 * with every accepted patch, and a stale tag is refused with `412`.
 */
function server(initial: UserPreferences = stored) {
  let document = initial;

  return {
    get document() {
      return document;
    },
    getPreferences: vi.fn(async () => ({
      data: document,
      etag: `"v${document.version}"`,
    })),
    updatePreferences: vi.fn(
      async (
        patch: UpdateUserPreferencesRequest,
        options: { ifMatch: string },
      ) => {
        if (options.ifMatch !== `"v${document.version}"`) {
          throw apiError(412);
        }

        document = {
          ...document,
          ...(patch.density ? { density: patch.density } : {}),
          ...(patch.reducedMotion === undefined
            ? {}
            : { reducedMotion: patch.reducedMotion }),
          ...(patch.screenReaderPagedMode === undefined
            ? {}
            : { screenReaderPagedMode: patch.screenReaderPagedMode }),
          version: document.version + 1,
        };
        return { data: document, etag: `"v${document.version}"` };
      },
    ),
  };
}

function deferred<T>() {
  let settle!: (value: T) => void;
  let fail!: (error: unknown) => void;
  const promise = new Promise<T>((resolve, reject) => {
    settle = resolve;
    fail = reject;
  });

  return { promise, settle, fail };
}

function start(gateway: PreferenceGateway, applied = device()) {
  const sync = new PreferenceSync(gateway, applied.apply);
  sync.adopt({ data: stored, etag: '"v3"' });
  return { sync, applied };
}

describe('preference synchronisation', () => {
  it('serialises rapid changes into ordered writes that keep every field', async () => {
    const api = server();
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    sync.save({ reducedMotion: true });
    sync.save({ screenReaderPagedMode: true });
    await sync.settled();

    expect(api.document).toMatchObject({
      density: 'compact',
      reducedMotion: true,
      screenReaderPagedMode: true,
    });
    expect(applied.current).toMatchObject({
      density: 'compact',
      reducedMotion: true,
      screenReaderPagedMode: true,
    });
    expect(sync.state).toMatchObject({ saving: false, saved: true });
    expect(sync.state.failure).toBeUndefined();
  });

  it('coalesces changes made while a write is in flight into one request', async () => {
    const api = server();
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    sync.save({ reducedMotion: true });
    sync.save({ screenReaderPagedMode: true });
    await sync.settled();

    expect(api.updatePreferences).toHaveBeenCalledTimes(2);
    expect(api.updatePreferences.mock.calls[0]?.[0]).toEqual({
      density: 'compact',
    });
    expect(api.updatePreferences.mock.calls[0]?.[1]).toEqual({
      ifMatch: '"v3"',
    });
    expect(api.updatePreferences.mock.calls[1]?.[0]).toEqual({
      reducedMotion: true,
      screenReaderPagedMode: true,
    });
    expect(api.updatePreferences.mock.calls[1]?.[1]).toEqual({
      ifMatch: '"v4"',
    });
  });

  it('never shows a server answer that is older than an unsent change', async () => {
    const api = server();
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    sync.save({ reducedMotion: true });
    await sync.settled();

    const chosen = applied.history.findIndex(
      (value) => value.reducedMotion === true,
    );
    expect(chosen).toBeGreaterThanOrEqual(0);
    expect(
      applied.history
        .slice(chosen)
        .every((value) => value.reducedMotion === true),
    ).toBe(true);
  });

  it('recovers from a stale tag by reloading and reapplying the change once', async () => {
    const api = server();
    const moved: UserPreferences = {
      density: 'comfortable',
      reducedMotion: false,
      screenReaderPagedMode: true,
      version: 9,
    };
    api.getPreferences.mockImplementation(async () => ({
      data: moved,
      etag: '"v9"',
    }));
    let attempts = 0;
    api.updatePreferences.mockImplementation(async (patch) => {
      attempts += 1;
      if (attempts === 1) {
        throw apiError(412);
      }

      const data: UserPreferences = {
        ...moved,
        ...(patch.density ? { density: patch.density } : {}),
        version: 10,
      };
      return { data, etag: '"v10"' };
    });
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();

    expect(api.getPreferences).toHaveBeenCalledTimes(1);
    expect(api.updatePreferences).toHaveBeenCalledTimes(2);
    expect(api.updatePreferences.mock.calls[1]?.[1]).toEqual({
      ifMatch: '"v9"',
    });
    expect(sync.state).toMatchObject({ saving: false, saved: true });
    expect(sync.state.failure).toBeUndefined();
    expect(applied.current).toMatchObject({
      density: 'compact',
      screenReaderPagedMode: true,
    });
  });

  it('surfaces a conflict that survives the reload and keeps the device choice', async () => {
    const api = server();
    api.updatePreferences.mockImplementation(async () => {
      throw apiError(412);
    });
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();

    expect(sync.state).toMatchObject({ saving: false, failure: 'conflict' });
    expect(applied.current.density).toBe('compact');
  });

  it('surfaces an unreachable API and keeps the device choice', async () => {
    const api = server();
    api.updatePreferences.mockImplementation(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync, applied } = start(api);

    sync.save({ reducedMotion: true });
    await sync.settled();

    expect(sync.state).toMatchObject({ saving: false, failure: 'unreachable' });
    expect(applied.current.reducedMotion).toBe(true);
    expect(api.getPreferences).not.toHaveBeenCalled();
  });

  it('surfaces the failure when the reload itself cannot be read', async () => {
    const api = server();
    api.updatePreferences.mockImplementation(async () => {
      throw apiError(412);
    });
    api.getPreferences.mockImplementation(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();

    expect(sync.state).toMatchObject({ failure: 'unreachable' });
  });

  it('holds a change made before the account document arrives', async () => {
    const api = server();
    const applied = device();
    const sync = new PreferenceSync(api, applied.apply);

    sync.save({ density: 'compact' });
    expect(api.updatePreferences).not.toHaveBeenCalled();

    sync.adopt({ data: stored, etag: '"v3"' });
    await sync.settled();

    expect(api.document.density).toBe('compact');
    expect(applied.current.density).toBe('compact');
  });

  it('publishes the account document when nothing local is waiting', async () => {
    const api = server();
    const applied = device();
    const sync = new PreferenceSync(api, applied.apply);

    sync.adopt({
      data: {
        density: 'compact',
        reducedMotion: true,
        screenReaderPagedMode: true,
        version: 9,
      },
      etag: '"v9"',
    });
    await sync.settled();

    expect(applied.current).toEqual({
      density: 'compact',
      reducedMotion: true,
      screenReaderPagedMode: true,
    });
    expect(api.updatePreferences).not.toHaveBeenCalled();
  });

  it('clears a reported failure once the document is read again', async () => {
    const api = server();
    api.updatePreferences.mockImplementation(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();
    expect(sync.state.failure).toBe('unreachable');

    sync.adopt({ data: { ...stored, version: 4 }, etag: '"v4"' });
    await sync.settled();

    expect(sync.state).toEqual({ saving: false, saved: false });
  });

  it('notifies subscribers while a change is in flight and once it lands', async () => {
    const api = server();
    const { sync } = start(api);
    const listener = vi.fn();
    sync.subscribe(listener);

    sync.save({ density: 'compact' });
    expect(sync.state.saving).toBe(true);
    await sync.settled();

    expect(listener).toHaveBeenCalled();
    expect(sync.state).toMatchObject({ saving: false, saved: true });
  });
});

describe('preferences that failed to save', () => {
  it('retries the change that failed with the next one the visitor makes', async () => {
    const api = server();
    const accepted = api.updatePreferences.getMockImplementation()!;
    api.updatePreferences.mockImplementationOnce(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();
    expect(sync.state).toMatchObject({ saved: false, failure: 'unreachable' });

    api.updatePreferences.mockImplementation(accepted);
    sync.save({ reducedMotion: true });
    await sync.settled();

    expect(api.document).toMatchObject({
      density: 'compact',
      reducedMotion: true,
    });
    expect(applied.current).toMatchObject({
      density: 'compact',
      reducedMotion: true,
    });
    expect(sync.state).toEqual({ saving: false, saved: true });
  });

  it('lets a newer choice overtake the one that failed', async () => {
    const api = server();
    const accepted = api.updatePreferences.getMockImplementation()!;
    api.updatePreferences.mockImplementationOnce(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();

    api.updatePreferences.mockImplementation(accepted);
    sync.save({ density: 'comfortable' });
    await sync.settled();

    expect(api.document.density).toBe('comfortable');
    expect(sync.state).toEqual({ saving: false, saved: true });
  });

  it('keeps reporting the failure while the retry is in flight', async () => {
    const api = server();
    const held = deferred<never>();
    api.updatePreferences.mockImplementationOnce(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync } = start(api);
    const seen: string[] = [];
    sync.subscribe(() => {
      seen.push(
        `${sync.state.saving ? 'saving' : 'idle'}:${sync.state.failure ?? 'none'}`,
      );
    });

    sync.save({ density: 'compact' });
    await sync.settled();

    api.updatePreferences.mockImplementation(async () => held.promise);
    sync.save({ reducedMotion: true });

    expect(sync.state).toMatchObject({
      saving: true,
      failure: 'unreachable',
    });
    expect(seen).toContain('saving:unreachable');

    held.fail(new TypeError('Failed to fetch'));
    await sync.settled();
  });

  it('never reports a save while a change is still queued', async () => {
    const api = server();
    api.updatePreferences.mockImplementation(async () => {
      throw new TypeError('Failed to fetch');
    });
    const { sync } = start(api);
    const saved: boolean[] = [];
    sync.subscribe(() => saved.push(sync.state.saved));

    sync.save({ density: 'compact' });
    await sync.settled();
    sync.save({ reducedMotion: true });
    await sync.settled();

    expect(saved).not.toContain(true);
    expect(sync.state.failure).toBe('unreachable');
  });

  it('keeps intent queued while a write is open and retries all of it', async () => {
    const api = server();
    const accepted = api.updatePreferences.getMockImplementation()!;
    const held = deferred<never>();
    api.updatePreferences.mockImplementationOnce(async () => held.promise);
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    sync.save({ reducedMotion: true });
    held.fail(new TypeError('Failed to fetch'));
    await sync.settled();

    expect(sync.state.failure).toBe('unreachable');

    api.updatePreferences.mockImplementation(accepted);
    sync.save({ screenReaderPagedMode: true });
    await sync.settled();

    expect(api.document).toMatchObject({
      density: 'compact',
      reducedMotion: true,
      screenReaderPagedMode: true,
    });
    expect(sync.state).toEqual({ saving: false, saved: true });
  });

  it('retries a conflict against the version the reload reported', async () => {
    const api = server();
    const moved: UserPreferences = { ...stored, version: 9 };
    api.getPreferences.mockImplementation(async () => ({
      data: moved,
      etag: '"v9"',
    }));
    api.updatePreferences.mockImplementation(async () => {
      throw apiError(412);
    });
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();
    expect(sync.state.failure).toBe('conflict');

    api.updatePreferences.mockImplementation(async (patch) => ({
      data: { ...moved, ...deviceOnly(patch), version: 10 },
      etag: '"v10"',
    }));
    sync.save({ reducedMotion: true });
    await sync.settled();

    expect(api.updatePreferences.mock.calls.at(-1)?.[0]).toEqual({
      density: 'compact',
      reducedMotion: true,
    });
    expect(api.updatePreferences.mock.calls.at(-1)?.[1]).toEqual({
      ifMatch: '"v9"',
    });
    expect(sync.state).toEqual({ saving: false, saved: true });
  });
});

describe('rereading the account document', () => {
  it('gives up the change that could not be stored', async () => {
    const api = server();
    api.updatePreferences.mockImplementation(async () => {
      throw apiError(412);
    });
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    await sync.settled();
    expect(sync.state.failure).toBe('conflict');

    sync.adopt({ data: { ...stored, version: 9 }, etag: '"v9"' });
    await sync.settled();

    expect(sync.state).toEqual({ saving: false, saved: false });
    expect(applied.current.density).toBe('comfortable');
    expect(api.updatePreferences).toHaveBeenCalledTimes(2);
  });

  it('keeps a queued change when nothing has failed', async () => {
    const api = server();
    const held = deferred<{ data: UserPreferences; etag: string }>();
    api.updatePreferences.mockImplementationOnce(async () => held.promise);
    const { sync } = start(api);

    sync.save({ density: 'compact' });
    sync.save({ reducedMotion: true });
    sync.adopt({ data: { ...stored, version: 3 }, etag: '"v3"' });
    held.settle({
      data: { ...stored, density: 'compact', version: 4 },
      etag: '"v4"',
    });
    await sync.settled();

    expect(api.updatePreferences.mock.calls.at(-1)?.[0]).toEqual({
      reducedMotion: true,
    });
  });
});

describe('preferences after the account is left', () => {
  it('never applies a write that lands once the screen is gone', async () => {
    const api = server();
    const held = deferred<{ data: UserPreferences; etag: string }>();
    api.updatePreferences.mockImplementation(async () => held.promise);
    const { sync, applied } = start(api);

    sync.save({ density: 'compact' });
    sync.dispose();
    held.settle({
      data: { ...stored, density: 'compact', version: 4 },
      etag: '"v4"',
    });
    await sync.settled();

    expect(applied.history.at(-1)?.density).toBe('compact');
    expect(api.updatePreferences).toHaveBeenCalledTimes(1);
    expect(sync.state).toEqual({ saving: false, saved: false });
  });

  it('sends nothing for an account that is no longer on screen', async () => {
    const api = server();
    const { sync, applied } = start(api);
    const seen = applied.history.length;

    sync.dispose();
    sync.save({ density: 'compact' });
    sync.adopt({ data: { ...stored, version: 7 }, etag: '"v7"' });
    await sync.settled();

    expect(api.updatePreferences).not.toHaveBeenCalled();
    expect(applied.history).toHaveLength(seen);
  });

  it('works again when a replayed effect reopens it', async () => {
    const api = server();
    const { sync } = start(api);

    sync.dispose();
    sync.open();
    sync.adopt({ data: stored, etag: '"v3"' });
    sync.save({ density: 'compact' });
    await sync.settled();

    expect(api.document.density).toBe('compact');
    expect(sync.state).toEqual({ saving: false, saved: true });
  });
});
