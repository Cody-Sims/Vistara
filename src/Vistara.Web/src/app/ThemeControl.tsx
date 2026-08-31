import {
  setThemePreference,
  useThemePreference,
  type ThemePreference,
} from './theme';
import styles from './ThemeControl.module.css';

export function ThemeControl() {
  const preference = useThemePreference();

  return (
    <label className={styles.control}>
      <span className={styles.label}>Theme</span>
      <select
        className={styles.select}
        aria-label="Theme"
        value={preference}
        onChange={(event) =>
          setThemePreference(event.target.value as ThemePreference)
        }
      >
        <option value="system">System</option>
        <option value="dark">Dark</option>
        <option value="light">Light</option>
      </select>
    </label>
  );
}
