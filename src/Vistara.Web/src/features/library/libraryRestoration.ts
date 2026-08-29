export interface LibraryRestorationState {
  scrollTop: number;
  focusedAssetId?: string;
}

export interface LibraryStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

const prefix = 'vistara:library:';

function isRestorationState(value: unknown): value is LibraryRestorationState {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Record<string, unknown>;

  return (
    typeof candidate.scrollTop === 'number' &&
    Number.isFinite(candidate.scrollTop) &&
    candidate.scrollTop >= 0 &&
    (candidate.focusedAssetId === undefined ||
      typeof candidate.focusedAssetId === 'string')
  );
}

export function createLibraryRestorationStore(storage: LibraryStorage) {
  return {
    read(address: string): LibraryRestorationState | null {
      try {
        const value: unknown = JSON.parse(storage.getItem(prefix + address) ?? '');
        return isRestorationState(value) ? value : null;
      } catch {
        return null;
      }
    },
    save(address: string, state: LibraryRestorationState) {
      storage.setItem(prefix + address, JSON.stringify(state));
    },
    remove(address: string) {
      storage.removeItem(prefix + address);
    },
  };
}
