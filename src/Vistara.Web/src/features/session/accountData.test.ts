import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import {
  accountScopedDatabases,
  clearAccountScopedData,
} from './accountData';

function fakeSessionStorage(entries: Record<string, string>) {
  const map = new Map(Object.entries(entries));
  return {
    get length() {
      return map.size;
    },
    key: (index: number) => [...map.keys()][index] ?? null,
    getItem: (key: string) => map.get(key) ?? null,
    removeItem: (key: string) => {
      map.delete(key);
    },
    entries: () => Object.fromEntries(map),
  };
}

function fakeIndexedDb() {
  const deleted: string[] = [];
  return {
    deleted,
    deleteDatabase: (name: string) => {
      deleted.push(name);
      const request = {
        onsuccess: null as (() => void) | null,
        onerror: null as (() => void) | null,
        onblocked: null as (() => void) | null,
      };
      queueMicrotask(() => request.onsuccess?.());
      return request as unknown as IDBOpenDBRequest;
    },
  };
}

describe('account-scoped data', () => {
  it('drops every cached response so the next account starts from the server', async () => {
    const queryCache = new QueryClient();
    queryCache.setQueryData(['assets'], { items: [{ id: 'private-asset' }] });

    await clearAccountScopedData({
      queryCache,
      sessionStorage: fakeSessionStorage({}),
      indexedDB: fakeIndexedDb(),
    });

    expect(queryCache.getQueryData(['assets'])).toBeUndefined();
    expect(queryCache.getQueryCache().getAll()).toHaveLength(0);
  });

  it('removes gallery state but keeps device preferences', async () => {
    const storage = fakeSessionStorage({
      'vistara:route-restoration:/library': '{"scrollTop":420}',
      'vistara:library:selection': '["asset-1"]',
      'vistara:theme': 'dark',
      'vistara:preferences': '{"density":"compact"}',
      'unrelated-app': 'keep me',
    });

    await clearAccountScopedData({
      queryCache: new QueryClient(),
      sessionStorage: storage,
      indexedDB: fakeIndexedDb(),
    });

    expect(storage.entries()).toEqual({
      'vistara:theme': 'dark',
      'vistara:preferences': '{"density":"compact"}',
      'unrelated-app': 'keep me',
    });
  });

  it('deletes the resumable upload database', async () => {
    const databases = fakeIndexedDb();

    await clearAccountScopedData({
      queryCache: new QueryClient(),
      sessionStorage: fakeSessionStorage({}),
      indexedDB: databases,
    });

    expect(databases.deleted).toEqual([...accountScopedDatabases]);
  });

  it('resolves even when the database refuses to be deleted', async () => {
    const deleteDatabase = vi.fn(() => {
      const request = {
        onsuccess: null as (() => void) | null,
        onerror: null as (() => void) | null,
        onblocked: null as (() => void) | null,
      };
      queueMicrotask(() => request.onblocked?.());
      return request as unknown as IDBOpenDBRequest;
    });

    await expect(
      clearAccountScopedData({
        queryCache: new QueryClient(),
        sessionStorage: fakeSessionStorage({}),
        indexedDB: { deleteDatabase },
      }),
    ).resolves.toBeUndefined();
    expect(deleteDatabase).toHaveBeenCalled();
  });

  it('isolates one account from the next on the same device', async () => {
    const queryCache = new QueryClient();
    const storage = fakeSessionStorage({
      'vistara:route-restoration:/library': '{"scrollTop":900}',
    });
    queryCache.setQueryData(['assets'], { owner: 'first-account' });

    await clearAccountScopedData({
      queryCache,
      sessionStorage: storage,
      indexedDB: fakeIndexedDb(),
    });

    // The second account signs in and populates its own cache.
    queryCache.setQueryData(['assets'], { owner: 'second-account' });

    expect(queryCache.getQueryData(['assets'])).toEqual({
      owner: 'second-account',
    });
    expect(storage.entries()).toEqual({});
  });
});
