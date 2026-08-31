export interface AccountDataStores {
  /** TanStack Query cache holding tenant-scoped responses. */
  readonly queryCache?: { clear(): void };
  readonly sessionStorage?: Pick<
    Storage,
    'key' | 'length' | 'removeItem' | 'getItem'
  >;
  readonly indexedDB?: Pick<IDBFactory, 'deleteDatabase'>;
}

/**
 * Databases and storage prefixes that hold data for the signed-in account.
 * Nothing here ever holds a credential or a signed grant; it is gallery state
 * that must not survive into the next account's session.
 */
export const accountScopedDatabases = ['vistara-upload-queue'] as const;
export const accountScopedStoragePrefix = 'vistara:';

/**
 * Preferences that describe the device rather than the account and therefore
 * stay when someone signs out.
 */
const devicePreferenceKeys = new Set(['vistara:theme', 'vistara:preferences']);

/**
 * Clears every cache and store that belongs to the account that just signed
 * out, so the next visitor on this device starts from the server.
 */
export async function clearAccountScopedData(
  stores: AccountDataStores = {},
): Promise<void> {
  stores.queryCache?.clear();

  const storage = stores.sessionStorage ?? readSessionStorage();
  if (storage) {
    const doomed: string[] = [];
    for (let index = 0; index < storage.length; index += 1) {
      const key = storage.key(index);
      if (
        key?.startsWith(accountScopedStoragePrefix) &&
        !devicePreferenceKeys.has(key)
      ) {
        doomed.push(key);
      }
    }

    for (const key of doomed) {
      storage.removeItem(key);
    }
  }

  const databases = stores.indexedDB ?? readIndexedDb();
  if (!databases) {
    return;
  }

  await Promise.all(
    accountScopedDatabases.map(
      (name) =>
        new Promise<void>((resolve) => {
          let request: IDBOpenDBRequest;
          try {
            request = databases.deleteDatabase(name);
          } catch {
            resolve();
            return;
          }

          request.onsuccess = () => resolve();
          request.onerror = () => resolve();
          request.onblocked = () => resolve();
        }),
    ),
  );
}

function readSessionStorage() {
  try {
    return typeof sessionStorage === 'undefined' ? undefined : sessionStorage;
  } catch {
    return undefined;
  }
}

function readIndexedDb() {
  try {
    return typeof indexedDB === 'undefined' ? undefined : indexedDB;
  } catch {
    return undefined;
  }
}
