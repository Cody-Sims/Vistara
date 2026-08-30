import { useState } from 'react';
import { Link } from 'react-router-dom';
import { VistaraApiError } from '../../api/generated/client';
import type { SessionPreferences } from '../../api/platform';
import {
  setThemePreference,
  useThemePreference,
  type ThemePreference,
} from '../../app/theme';
import { describeRole, useSession } from '../session';
import styles from './settings.module.css';

type SaveState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'saved' }
  | { readonly kind: 'conflict' }
  | { readonly kind: 'failed'; readonly message: string };

const themeChoices: readonly {
  value: ThemePreference;
  label: string;
  hint: string;
}[] = [
  {
    value: 'system',
    label: 'System',
    hint: 'Follow the appearance chosen for this device.',
  },
  { value: 'light', label: 'Light', hint: 'A bright workspace for daylight.' },
  { value: 'dark', label: 'Dark', hint: 'A quiet workspace for dim rooms.' },
];

const densityChoices = [
  {
    value: 'comfortable' as const,
    label: 'Comfortable',
    hint: 'Roomier rows and larger targets.',
  },
  {
    value: 'compact' as const,
    label: 'Compact',
    hint: 'More images in view on large screens.',
  },
];

export function SettingsPage() {
  const session = useSession();
  const theme = useThemePreference();
  const saved = session.session?.preferences ?? {};
  const [draft, setDraft] = useState<SessionPreferences>(saved);
  const [state, setState] = useState<SaveState>({ kind: 'idle' });

  const value = { ...saved, ...draft };
  const dirty =
    value.density !== saved.density ||
    value.reducedMotion !== saved.reducedMotion ||
    value.screenReaderPagedMode !== saved.screenReaderPagedMode;

  async function save() {
    setState({ kind: 'saving' });
    try {
      await session.savePreferences({
        density: value.density ?? 'comfortable',
        reducedMotion: value.reducedMotion ?? false,
        screenReaderPagedMode: value.screenReaderPagedMode ?? false,
        ...(value.locale ? { locale: value.locale } : {}),
      });
      setDraft({});
      setState({ kind: 'saved' });
    } catch (error) {
      if (error instanceof VistaraApiError && error.status === 409) {
        setState({ kind: 'conflict' });
        return;
      }

      setState({
        kind: 'failed',
        message:
          error instanceof VistaraApiError && error.status === 403
            ? 'Preferences could not be saved because this account may not change them.'
            : 'Preferences could not be saved. Check your connection and try again.',
      });
    }
  }

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <p className={styles.eyebrow}>Your workspace</p>
        <h1>Settings</h1>
        <p className={styles.description}>
          Appearance is stored on this device. Reading preferences follow your
          account to every device.
        </p>
      </header>

      <section className={styles.card} aria-labelledby="settings-account">
        <h2 id="settings-account">Account</h2>
        <dl className={styles.details}>
          <div>
            <dt>Name</dt>
            <dd>{session.user?.displayName ?? 'Not signed in'}</dd>
          </div>
          <div>
            <dt>Email</dt>
            <dd>{session.user?.email ?? '—'}</dd>
          </div>
          <div>
            <dt>Workspace</dt>
            <dd>{session.membership?.tenantName ?? '—'}</dd>
          </div>
          <div>
            <dt>Role</dt>
            <dd>{describeRole(session.role)}</dd>
          </div>
        </dl>
        <div className={styles.actions}>
          <button
            className={styles.secondaryButton}
            type="button"
            onClick={() => void session.signOut()}
          >
            Sign out
          </button>
          {session.canAdminister ? (
            <Link className={styles.secondaryLink} to="/admin/users">
              Open administration
            </Link>
          ) : null}
        </div>
      </section>

      <section className={styles.card} aria-labelledby="settings-appearance">
        <h2 id="settings-appearance">Appearance</h2>
        <fieldset className={styles.choices}>
          <legend className={styles.legend}>Theme</legend>
          {themeChoices.map((choice) => (
            <label className={styles.choice} key={choice.value}>
              <input
                aria-describedby={`theme-${choice.value}-hint`}
                aria-label={choice.label}
                checked={theme === choice.value}
                name="theme"
                type="radio"
                value={choice.value}
                onChange={() => setThemePreference(choice.value)}
              />
              <span>
                <strong>{choice.label}</strong>
                <span
                  className={styles.choiceHint}
                  id={`theme-${choice.value}-hint`}
                >
                  {choice.hint}
                </span>
              </span>
            </label>
          ))}
        </fieldset>
      </section>

      <section className={styles.card} aria-labelledby="settings-reading">
        <h2 id="settings-reading">Reading and motion</h2>
        <fieldset className={styles.choices}>
          <legend className={styles.legend}>Grid density</legend>
          {densityChoices.map((choice) => (
            <label className={styles.choice} key={choice.value}>
              <input
                aria-describedby={`density-${choice.value}-hint`}
                aria-label={choice.label}
                checked={(value.density ?? 'comfortable') === choice.value}
                name="density"
                type="radio"
                value={choice.value}
                onChange={() =>
                  setDraft((current) => ({
                    ...current,
                    density: choice.value,
                  }))
                }
              />
              <span>
                <strong>{choice.label}</strong>
                <span
                  className={styles.choiceHint}
                  id={`density-${choice.value}-hint`}
                >
                  {choice.hint}
                </span>
              </span>
            </label>
          ))}
        </fieldset>

        <label className={styles.toggle}>
          <input
            aria-describedby="reduced-motion-hint"
            aria-label="Reduce motion"
            checked={value.reducedMotion ?? false}
            type="checkbox"
            onChange={(event) =>
              setDraft((current) => ({
                ...current,
                reducedMotion: event.target.checked,
              }))
            }
          />
          <span>
            <strong>Reduce motion</strong>
            <span className={styles.choiceHint} id="reduced-motion-hint">
              Skip non-essential transitions everywhere you sign in.
            </span>
          </span>
        </label>

        <label className={styles.toggle}>
          <input
            aria-describedby="paged-mode-hint"
            aria-label="Paged library mode"
            checked={value.screenReaderPagedMode ?? false}
            type="checkbox"
            onChange={(event) =>
              setDraft((current) => ({
                ...current,
                screenReaderPagedMode: event.target.checked,
              }))
            }
          />
          <span>
            <strong>Paged library mode</strong>
            <span className={styles.choiceHint} id="paged-mode-hint">
              Replace endless scrolling with numbered pages for screen readers.
            </span>
          </span>
        </label>

        <div className={styles.actions}>
          <button
            className={styles.primaryButton}
            disabled={state.kind === 'saving'}
            type="button"
            onClick={() => void save()}
          >
            {state.kind === 'saving' ? 'Saving…' : 'Save preferences'}
          </button>
          {state.kind === 'conflict' ? (
            <button
              className={styles.secondaryButton}
              type="button"
              onClick={() => {
                setDraft({});
                setState({ kind: 'idle' });
                void session.reload();
              }}
            >
              Reload preferences
            </button>
          ) : null}
          {dirty && state.kind === 'idle' ? (
            <span className={styles.hint}>Unsaved changes</span>
          ) : null}
        </div>

        <p className={styles.saveStatus} role="status" aria-live="polite">
          {state.kind === 'saved' ? 'Preferences saved.' : ''}
        </p>

        {state.kind === 'failed' ? (
          <p className={styles.failure} role="alert">
            {state.message}
          </p>
        ) : null}
        {state.kind === 'conflict' ? (
          <p className={styles.failure} role="alert">
            Your preferences changed somewhere else. Reload them before saving
            again so nothing is overwritten.
          </p>
        ) : null}
      </section>
    </div>
  );
}
