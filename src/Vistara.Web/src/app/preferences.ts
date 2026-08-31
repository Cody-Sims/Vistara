import { useSyncExternalStore } from 'react';

export type Density = 'comfortable' | 'compact';

export interface AppPreferences {
  readonly density: Density;
  readonly reducedMotion: boolean;
  /**
   * Replaces endless scrolling with explicit pages so a screen reader or
   * keyboard user can reach the end of a list.
   */
  readonly screenReaderPagedMode: boolean;
}

const storageKey = 'vistara:preferences';
const listeners = new Set<() => void>();

export const defaultPreferences: AppPreferences = {
  density: 'comfortable',
  reducedMotion: false,
  screenReaderPagedMode: false,
};

let preferences: AppPreferences = defaultPreferences;
let loaded = false;

function read(): AppPreferences {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) {
      return defaultPreferences;
    }

    const parsed = JSON.parse(raw) as Partial<AppPreferences>;
    return {
      density: parsed.density === 'compact' ? 'compact' : 'comfortable',
      reducedMotion: parsed.reducedMotion === true,
      screenReaderPagedMode: parsed.screenReaderPagedMode === true,
    };
  } catch {
    return defaultPreferences;
  }
}

export function applyDocumentPreferences(value = preferences) {
  const root = document.documentElement;

  root.dataset.density = value.density;
  root.dataset.reducedMotion = String(value.reducedMotion);
  root.dataset.pagedMode = String(value.screenReaderPagedMode);
}

function publish() {
  applyDocumentPreferences();
  for (const listener of listeners) {
    listener();
  }
}

function subscribe(onStoreChange: () => void) {
  if (listeners.size === 0) {
    preferences = read();
    loaded = true;
    applyDocumentPreferences();
  }

  listeners.add(onStoreChange);
  return () => {
    listeners.delete(onStoreChange);
  };
}

export function getPreferences(): AppPreferences {
  if (!loaded) {
    preferences = read();
    loaded = true;
  }

  return preferences;
}

export function setPreferences(update: Partial<AppPreferences>) {
  preferences = { ...getPreferences(), ...update };
  try {
    localStorage.setItem(storageKey, JSON.stringify(preferences));
  } catch {
    // The choice still applies for this session when storage is unavailable.
  }

  publish();
}

/** Forgets the stored preferences; used by tests and by a device reset. */
export function resetPreferences() {
  preferences = defaultPreferences;
  loaded = false;
  try {
    localStorage.removeItem(storageKey);
  } catch {
    // Nothing to forget when storage is unavailable.
  }
}

export function useAppPreferences(): AppPreferences {
  return useSyncExternalStore(subscribe, getPreferences, () => preferences);
}
