import { describe, expect, it } from 'vitest';
import { createLibraryRestorationStore } from './libraryRestoration';

describe('library restoration state', () => {
  it('round-trips scroll and focused asset state by addressable library URL', () => {
    const values = new Map<string, string>();
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    };
    const store = createLibraryRestorationStore(storage);

    store.save('/library?q=snow', {
      scrollTop: 640,
      focusedAssetId: 'asset-22',
    });

    expect(store.read('/library?q=snow')).toEqual({
      scrollTop: 640,
      focusedAssetId: 'asset-22',
    });
    expect(store.read('/library?q=portraits')).toBeNull();
  });

  it('discards malformed or unsafe persisted values', () => {
    const storage = {
      getItem: () => '{"scrollTop":-1,"focusedAssetId":7}',
      setItem: () => undefined,
      removeItem: () => undefined,
    };

    expect(createLibraryRestorationStore(storage).read('/library')).toBeNull();
  });
});
