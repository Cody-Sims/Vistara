import { useSyncExternalStore } from 'react';

export type ThemePreference = 'dark' | 'light' | 'system';

const themeStorageKey = 'vistara:theme';
const darkThemeQuery = '(prefers-color-scheme: dark)';
const listeners = new Set<() => void>();

let preference: ThemePreference = readStoredPreference();
let media: MediaQueryList | undefined;

function readStoredPreference(): ThemePreference {
  try {
    const saved = localStorage.getItem(themeStorageKey);
    return saved === 'dark' || saved === 'light' || saved === 'system'
      ? saved
      : 'system';
  } catch {
    return 'system';
  }
}

function darkThemeMedia(): MediaQueryList | undefined {
  if (media) {
    return media;
  }

  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return undefined;
  }

  media = window.matchMedia(darkThemeQuery);
  return media;
}

export function resolveTheme(
  value: ThemePreference = preference,
): 'dark' | 'light' {
  if (value !== 'system') {
    return value;
  }

  return darkThemeMedia()?.matches ? 'dark' : 'light';
}

function applyDocumentTheme() {
  const theme = resolveTheme();
  const root = document.documentElement;

  root.dataset.theme = theme;
  root.dataset.themePreference = preference;
  root.style.colorScheme = theme;
  document
    .querySelector<HTMLMetaElement>('meta[name="theme-color"]')
    ?.setAttribute('content', theme === 'dark' ? '#11110f' : '#f2f0e9');
}

function publish() {
  applyDocumentTheme();
  for (const listener of listeners) {
    listener();
  }
}

function subscribe(onStoreChange: () => void) {
  if (listeners.size === 0) {
    preference = readStoredPreference();
    darkThemeMedia()?.addEventListener('change', publish);
    applyDocumentTheme();
  }

  listeners.add(onStoreChange);
  return () => {
    listeners.delete(onStoreChange);
    if (listeners.size === 0) {
      darkThemeMedia()?.removeEventListener('change', publish);
    }
  };
}

export function setThemePreference(next: ThemePreference) {
  preference = next;
  try {
    localStorage.setItem(themeStorageKey, next);
  } catch {
    // The choice still applies for this session when storage is unavailable.
  }

  publish();
}

export function useThemePreference(): ThemePreference {
  return useSyncExternalStore(
    subscribe,
    () => preference,
    () => 'system' as const,
  );
}
