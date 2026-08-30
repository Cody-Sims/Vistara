import { useEffect, useState } from 'react';
import styles from './ThemeControl.module.css';

type ThemePreference = 'dark' | 'light' | 'system';

const darkThemeQuery = '(prefers-color-scheme: dark)';
const themeStorageKey = 'vistara:theme';

export function ThemeControl() {
  const [preference, setPreference] = useState<ThemePreference>(readPreference);

  useEffect(() => {
    const media = getDarkThemeMedia();
    const apply = () => applyTheme(preference, media.matches);

    apply();
    if (preference !== 'system') {
      return;
    }

    media.addEventListener('change', apply);
    return () => media.removeEventListener('change', apply);
  }, [preference]);

  return (
    <label className={styles.control}>
      <span className={styles.label}>Theme</span>
      <select
        className={styles.select}
        aria-label="Theme"
        value={preference}
        onChange={(event) => {
          const nextPreference = event.target.value as ThemePreference;
          setPreference(nextPreference);
          try {
            localStorage.setItem(themeStorageKey, nextPreference);
          } catch {
            // Theme still applies for the session when storage is unavailable.
          }
        }}
      >
        <option value="system">System</option>
        <option value="dark">Dark</option>
        <option value="light">Light</option>
      </select>
    </label>
  );
}

function readPreference(): ThemePreference {
  try {
    const saved = localStorage.getItem(themeStorageKey);
    return saved === 'dark' || saved === 'light' || saved === 'system'
      ? saved
      : 'system';
  } catch {
    return 'system';
  }
}

function getDarkThemeMedia(): MediaQueryList {
  if (typeof window.matchMedia === 'function') {
    return window.matchMedia(darkThemeQuery);
  }

  return {
    matches: false,
    media: darkThemeQuery,
    onchange: null,
    addListener: () => undefined,
    removeListener: () => undefined,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    dispatchEvent: () => false,
  };
}

function applyTheme(preference: ThemePreference, systemIsDark: boolean) {
  const theme =
    preference === 'system' ? (systemIsDark ? 'dark' : 'light') : preference;
  const root = document.documentElement;

  root.dataset.theme = theme;
  root.dataset.themePreference = preference;
  root.style.colorScheme = theme;
  document
    .querySelector<HTMLMetaElement>('meta[name="theme-color"]')
    ?.setAttribute('content', theme === 'dark' ? '#11110f' : '#f2f0e9');
}
