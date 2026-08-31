import { useEffect, useMemo, useSyncExternalStore } from 'react';
import type { EntityTag } from '../../api/generated/models';
import type {
  UpdateUserPreferencesRequest,
  UserPreferences,
  VersionedResult,
} from '../../api/platform';
import { isStaleVersion, versionTag } from '../../api/versionTag';
import { setPreferences, type AppPreferences } from '../../app/preferences';

export interface PreferenceGateway {
  getPreferences(): Promise<VersionedResult<UserPreferences>>;
  updatePreferences(
    request: UpdateUserPreferencesRequest,
    options: { ifMatch: EntityTag },
  ): Promise<VersionedResult<UserPreferences>>;
}

export type PreferenceSyncFailure =
  /** The record moved on again while the reloaded edit was being reapplied. */
  | 'conflict'
  /** The account could not be reached at all. */
  | 'unreachable';

export interface PreferenceSyncState {
  readonly saving: boolean;
  readonly saved: boolean;
  readonly failure?: PreferenceSyncFailure;
}

const idle: PreferenceSyncState = { saving: false, saved: false };

function devicePreferences(document: UserPreferences): Partial<AppPreferences> {
  return {
    density: document.density,
    reducedMotion: document.reducedMotion,
    screenReaderPagedMode: document.screenReaderPagedMode,
  };
}

function deviceIntent(
  patch: UpdateUserPreferencesRequest,
): Partial<AppPreferences> {
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

/**
 * Keeps the account's preference document and this device in step across
 * changes made faster than the API can answer them.
 *
 * `PATCH /api/v1/me/preferences` requires `If-Match`, so two changes sent side
 * by side race: the second carries a tag the first already retired and is
 * refused with `412`. Every change is therefore queued rather than sent, one
 * write is in flight at a time, and anything chosen while that write is open
 * is merged into the next one. A refused write reloads the version the record
 * moved to and reapplies the same intent once.
 *
 * A change that still cannot be stored is not dropped: it goes back to the
 * front of the queue, under anything newer, so the next change the visitor
 * makes carries it too against the version currently known. The failure is
 * reported until the queue actually empties — it survives the retries in
 * between, and a save is never reported while anything is still unsent. Only
 * reading the document again answers it. Local intent is always layered over a
 * server answer, so an older document can never undo a change that has not
 * been sent yet.
 */
export class PreferenceSync {
  readonly #gateway: PreferenceGateway;
  readonly #apply: (value: Partial<AppPreferences>) => void;
  readonly #listeners = new Set<() => void>();
  #base: VersionedResult<UserPreferences> | undefined;
  #adopted: VersionedResult<UserPreferences> | undefined;
  #pending: UpdateUserPreferencesRequest | undefined;
  #running: Promise<void> | undefined;
  #state: PreferenceSyncState = idle;
  #closed = false;

  constructor(
    gateway: PreferenceGateway,
    apply: (value: Partial<AppPreferences>) => void = setPreferences,
  ) {
    this.#gateway = gateway;
    this.#apply = apply;
  }

  get state(): PreferenceSyncState {
    return this.#state;
  }

  readonly readState = (): PreferenceSyncState => this.#state;

  readonly subscribe = (listener: () => void): (() => void) => {
    this.#listeners.add(listener);
    return () => {
      this.#listeners.delete(listener);
    };
  };

  /**
   * Takes the document the account was read with, or reread with. Ignores a
   * result already taken so a re-render never replays an older version over a
   * newer one.
   *
   * Rereading after a reported failure is how the visitor asks for the
   * account's own document, so the change that could not be stored is given up
   * with the failure it caused. A reread while nothing has failed keeps
   * whatever is queued, including a change made before the document arrived.
   */
  readonly adopt = (result: VersionedResult<UserPreferences>): void => {
    if (this.#closed || this.#adopted === result) {
      return;
    }

    this.#adopted = result;
    this.#base = result;
    if (this.#state.failure) {
      this.#pending = undefined;
    }

    this.#publishDocument(result.data);
    if (this.#state !== idle) {
      this.#publish(idle);
    }

    this.#start();
  };

  /** Applies a change to this device and queues it for the account. */
  readonly save = (patch: UpdateUserPreferencesRequest): void => {
    if (this.#closed) {
      return;
    }

    this.#pending = { ...this.#pending, ...patch };
    this.#apply(deviceIntent(patch));
    this.#start();
  };

  /** Resolves once nothing is queued or in flight. */
  readonly settled = (): Promise<void> => this.#running ?? Promise.resolve();

  /**
   * Abandons the account this was reading and writing for, so a write that
   * lands after the screen is gone, or after another account signs in, is
   * never applied to whoever is here now.
   */
  readonly dispose = (): void => {
    this.#closed = true;
    this.#pending = undefined;
    this.#state = idle;
  };

  /** Reopens a synchroniser whose mount was replayed after a cleanup. */
  readonly open = (): void => {
    this.#closed = false;
  };

  #start(): void {
    if (this.#closed || this.#running || !this.#pending || !this.#base) {
      return;
    }

    // A retry does not answer the failure it is retrying; only an emptied
    // queue or a reread document does.
    this.#publish({
      saving: true,
      saved: false,
      ...(this.#state.failure ? { failure: this.#state.failure } : {}),
    });
    this.#running = this.#drain().finally(() => {
      this.#running = undefined;
    });
  }

  async #drain(): Promise<void> {
    while (!this.#closed && this.#pending && this.#base) {
      const patch: UpdateUserPreferencesRequest = this.#pending;
      this.#pending = undefined;

      const outcome = await this.#send(patch);
      if (this.#closed) {
        return;
      }

      if (outcome !== 'saved') {
        // The change is still the visitor's intent, so it waits under
        // anything chosen while the write was open for the next one to carry.
        this.#pending = { ...patch, ...this.#queued() };
        this.#publish({ saving: false, saved: false, failure: outcome });
        return;
      }
    }

    if (this.#closed || this.#pending) {
      return;
    }

    this.#publish({ saving: false, saved: true });
  }

  async #send(
    patch: UpdateUserPreferencesRequest,
  ): Promise<'saved' | PreferenceSyncFailure> {
    for (let attempt = 0; attempt < 2; attempt += 1) {
      try {
        const result = await this.#gateway.updatePreferences(patch, {
          ifMatch: this.#tag(),
        });
        if (this.#closed) {
          return 'saved';
        }

        this.#base = result;
        // The write was accepted, so this patch is part of the stored
        // document; layering it keeps the device steady even if the answer
        // trails behind, and anything queued since stays on top of both.
        this.#publishDocument(result.data, patch);
        return 'saved';
      } catch (error) {
        if (!isStaleVersion(error)) {
          return 'unreachable';
        }

        if (attempt > 0) {
          return 'conflict';
        }

        if (!(await this.#reload(patch))) {
          return 'unreachable';
        }
      }
    }

    return 'conflict';
  }

  /** Reads the version the record moved to, keeping this device's intent. */
  async #reload(patch: UpdateUserPreferencesRequest): Promise<boolean> {
    try {
      const latest = await this.#gateway.getPreferences();
      if (this.#closed) {
        return false;
      }

      this.#base = latest;
      this.#apply({
        ...devicePreferences(latest.data),
        ...deviceIntent(patch),
        ...deviceIntent(this.#pending ?? {}),
      });
      return true;
    } catch {
      return false;
    }
  }

  /**
   * Whatever is queued right now. Read through a call so it is not the value
   * the caller saw before it awaited a write.
   */
  #queued(): UpdateUserPreferencesRequest {
    return this.#pending ?? {};
  }

  #tag(): EntityTag {
    const base = this.#base;
    if (!base) {
      throw new Error('No preference document has been read yet.');
    }

    return base.etag ?? versionTag(base.data.version);
  }

  #publishDocument(
    document: UserPreferences,
    accepted: UpdateUserPreferencesRequest = {},
  ): void {
    this.#apply({
      ...devicePreferences(document),
      ...deviceIntent(accepted),
      ...deviceIntent(this.#pending ?? {}),
    });
  }

  #publish(state: PreferenceSyncState): void {
    if (this.#closed) {
      return;
    }

    this.#state = state;
    for (const listener of this.#listeners) {
      listener();
    }
  }
}

export function usePreferenceSync(gateway: PreferenceGateway): {
  readonly sync: PreferenceSync;
  readonly state: PreferenceSyncState;
} {
  const sync = useMemo(() => new PreferenceSync(gateway), [gateway]);
  const state = useSyncExternalStore(sync.subscribe, sync.readState);

  // Leaving the screen, or arriving at another account, abandons whatever is
  // still queued rather than letting it land on whoever is here next.
  useEffect(() => {
    sync.open();
    return () => sync.dispose();
  }, [sync]);

  return { sync, state };
}
