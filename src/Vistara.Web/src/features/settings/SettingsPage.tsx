import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import type {
  ApiKeyCollection,
  PlatformApiClient,
  TenantCollection,
  UpdateUserPreferencesRequest,
  UserPreferences,
} from '../../api/platform';
import { isStaleVersion, versionTag } from '../../api/versionTag';
import { useRemoteResource } from '../../app/useRemoteResource';
import {
  setPreferences,
  useAppPreferences,
  type Density,
} from '../../app/preferences';
import {
  setThemePreference,
  useThemePreference,
  type ThemePreference,
} from '../../app/theme';
import { describeRole, useSession } from '../session';
import styles from './settings.module.css';

export type SettingsClient = Pick<
  PlatformApiClient,
  | 'listTenants'
  | 'listApiKeys'
  | 'createApiKey'
  | 'revokeApiKey'
  | 'getPreferences'
  | 'updatePreferences'
>;

interface SettingsPageProps {
  readonly client: SettingsClient;
}

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

const densityChoices: readonly {
  value: Density;
  label: string;
  hint: string;
}[] = [
  {
    value: 'comfortable',
    label: 'Comfortable',
    hint: 'Roomier rows and larger spacing.',
  },
  {
    value: 'compact',
    label: 'Compact',
    hint: 'More images in view. Touch targets stay full size.',
  },
];

export function SettingsPage({ client }: SettingsPageProps) {
  const session = useSession();
  const theme = useThemePreference();

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <p className={styles.eyebrow}>Your workspace</p>
        <h1>Settings</h1>
        <p className={styles.description}>
          Appearance is stored on this device. Reading preferences follow your
          account, and workspace details come from the server.
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
            <dd>{session.membership?.name ?? '—'}</dd>
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

      <WorkspaceList client={client} activeTenantId={session.user?.tenantId} />

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

      <ReadingPreferences client={client} />

      <ApiKeySection client={client} />
    </div>
  );
}

function ReadingPreferences({ client }: { readonly client: SettingsClient }) {
  const preferences = useAppPreferences();
  const load = useCallback(() => client.getPreferences(), [client]);
  const { state, reload } = useRemoteResource<{
    data: UserPreferences;
    etag?: string;
  }>(load);
  const [applied, setApplied] = useState<UserPreferences>();
  const [saving, setSaving] = useState(false);
  const [failure, setFailure] = useState('');
  const [confirmation, setConfirmation] = useState('');

  // The account's stored preferences win when they arrive or are reloaded, and
  // are applied to the document so every view honours them immediately. A save
  // does not re-run this, so a just-made choice is never reverted.
  if (state.kind === 'ready' && applied !== state.value.data) {
    setApplied(state.value.data);
    setPreferences({
      density: state.value.data.density,
      reducedMotion: state.value.data.reducedMotion,
      screenReaderPagedMode: state.value.data.screenReaderPagedMode,
    });
  }

  async function save(patch: UpdateUserPreferencesRequest) {
    setPreferences({
      ...(patch.density ? { density: patch.density } : {}),
      ...(patch.reducedMotion === undefined
        ? {}
        : { reducedMotion: patch.reducedMotion }),
      ...(patch.screenReaderPagedMode === undefined
        ? {}
        : { screenReaderPagedMode: patch.screenReaderPagedMode }),
    });

    if (state.kind !== 'ready') {
      return;
    }

    setSaving(true);
    setFailure('');
    setConfirmation('');
    try {
      await client.updatePreferences(patch, {
        ifMatch: state.value.etag ?? versionTag(state.value.data.version),
      });
      setConfirmation('Preferences saved to your account.');
    } catch (error) {
      setFailure(
        isStaleVersion(error)
          ? 'Your preferences changed on another device. Reload them before saving again.'
          : 'Preferences could not be saved to your account. They still apply on this device.',
      );
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className={styles.card} aria-labelledby="settings-reading">
      <h2 id="settings-reading">Reading and motion</h2>
      <fieldset className={styles.choices} disabled={saving}>
        <legend className={styles.legend}>Grid density</legend>
        {densityChoices.map((choice) => (
          <label className={styles.choice} key={choice.value}>
            <input
              aria-describedby={`density-${choice.value}-hint`}
              aria-label={choice.label}
              checked={preferences.density === choice.value}
              name="density"
              type="radio"
              value={choice.value}
              onChange={() => void save({ density: choice.value })}
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
          checked={preferences.reducedMotion}
          type="checkbox"
          onChange={(event) =>
            void save({ reducedMotion: event.target.checked })
          }
        />
        <span>
          <strong>Reduce motion</strong>
          <span className={styles.choiceHint} id="reduced-motion-hint">
            Skip transitions and shimmering placeholders everywhere.
          </span>
        </span>
      </label>

      <label className={styles.toggle}>
        <input
          aria-describedby="paged-mode-hint"
          aria-label="Paged library and search"
          checked={preferences.screenReaderPagedMode}
          type="checkbox"
          onChange={(event) =>
            void save({ screenReaderPagedMode: event.target.checked })
          }
        />
        <span>
          <strong>Paged library and search</strong>
          <span className={styles.choiceHint} id="paged-mode-hint">
            Replace endless scrolling with numbered pages that can be reached
            from the keyboard.
          </span>
        </span>
      </label>

      <p className={styles.saveStatus} role="status" aria-live="polite">
        {confirmation}
      </p>

      {failure ? (
        <p className={styles.failure} role="alert">
          {failure}
          <button
            className={styles.secondaryButton}
            type="button"
            onClick={() => {
              setFailure('');
              setApplied(undefined);
              reload();
            }}
          >
            Reload preferences
          </button>
        </p>
      ) : null}

      {state.kind === 'failed' ? (
        <p className={styles.hint}>
          Account preferences could not be read, so these choices are stored on
          this device only.
        </p>
      ) : null}
    </section>
  );
}

function WorkspaceList({
  activeTenantId,
  client,
}: {
  readonly activeTenantId?: string;
  readonly client: SettingsClient;
}) {
  const load = useCallback(() => client.listTenants(), [client]);
  const { state } = useRemoteResource<TenantCollection>(load);

  return (
    <section className={styles.card} aria-labelledby="settings-workspaces">
      <h2 id="settings-workspaces">Workspaces</h2>
      {state.kind === 'loading' ? (
        <p className={styles.hint} role="status">
          Loading workspaces…
        </p>
      ) : null}
      {state.kind === 'failed' ? (
        <p className={styles.hint}>
          Workspaces could not be read. Your access is unchanged.
        </p>
      ) : null}
      {state.kind === 'ready' ? (
        <ul className={styles.plainList} aria-label="Workspaces">
          {state.value.items.map((tenant) => (
            <li className={styles.listRow} key={tenant.id}>
              <span>
                <strong>{tenant.name}</strong>
                <span className={styles.choiceHint}>
                  {tenant.slug} · {describeRole(tenant.role)}
                </span>
              </span>
              {tenant.id === activeTenantId ? (
                <span className={styles.badge}>Signed in here</span>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}

function ApiKeySection({ client }: { readonly client: SettingsClient }) {
  const load = useCallback(() => client.listApiKeys(), [client]);
  const { state, refresh } = useRemoteResource<ApiKeyCollection>(load);
  const [scopes, setScopes] = useState('');
  const [secret, setSecret] = useState('');
  const [failure, setFailure] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState<{ id: string; prefix: string }>();

  async function create() {
    setBusy(true);
    setFailure('');
    try {
      const requested = scopes
        .split(/[\s,]+/)
        .map((scope) => scope.trim())
        .filter(Boolean);
      if (requested.length === 0) {
        setFailure('Name at least one scope for the key.');
        return;
      }

      const result = await client.createApiKey({ scopes: requested });
      setSecret(result.secret);
      setScopes('');
      await refresh();
    } catch {
      setFailure(
        'The API key could not be created. Existing keys are unchanged.',
      );
    } finally {
      setBusy(false);
    }
  }

  async function revoke(keyId: string) {
    setBusy(true);
    setFailure('');
    try {
      await client.revokeApiKey(keyId);
      setConfirming(undefined);
      await refresh();
    } catch {
      setFailure('The API key could not be revoked. Nothing changed.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className={styles.card} aria-labelledby="settings-api-keys">
      <h2 id="settings-api-keys">API keys</h2>
      <p className={styles.hint}>
        Keys let scripts reach this workspace. The secret is shown once when the
        key is created and is never stored in the browser.
      </p>

      {failure ? (
        <p className={styles.failure} role="alert">
          {failure}
        </p>
      ) : null}

      {secret ? (
        <div className={styles.secret}>
          <p className={styles.hint}>
            Copy this secret now. It cannot be shown again.
          </p>
          <code>{secret}</code>
          <button
            className={styles.secondaryButton}
            type="button"
            onClick={() => setSecret('')}
          >
            I saved the secret
          </button>
        </div>
      ) : null}

      {state.kind === 'loading' ? (
        <p className={styles.hint} role="status">
          Loading API keys…
        </p>
      ) : null}

      {state.kind === 'failed' ? (
        <p className={styles.hint}>
          API keys could not be read. Existing keys keep working.
        </p>
      ) : null}

      {state.kind === 'ready' && state.value.items.length === 0 ? (
        <p className={styles.hint}>No API keys have been created yet.</p>
      ) : null}

      {state.kind === 'ready' && state.value.items.length > 0 ? (
        <ul className={styles.plainList} aria-label="API keys">
          {state.value.items.map((key) => (
            <li className={styles.listRow} key={key.id}>
              <span>
                <strong>{key.prefix}</strong>
                <span className={styles.choiceHint}>
                  {key.scopes.join(', ') || 'No scopes'} · {key.status}
                </span>
              </span>
              {confirming?.id === key.id ? (
                <span className={styles.actions}>
                  <button
                    className={styles.secondaryButton}
                    disabled={busy}
                    type="button"
                    onClick={() => void revoke(key.id)}
                  >
                    Revoke key
                  </button>
                  <button
                    className={styles.secondaryButton}
                    type="button"
                    onClick={() => setConfirming(undefined)}
                  >
                    Keep key
                  </button>
                </span>
              ) : (
                <button
                  aria-label={`Revoke ${key.prefix}`}
                  className={styles.secondaryButton}
                  disabled={busy}
                  type="button"
                  onClick={() => setConfirming({ id: key.id, prefix: key.prefix })}
                >
                  Revoke
                </button>
              )}
            </li>
          ))}
        </ul>
      ) : null}

      <form
        className={styles.inlineForm}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void create();
        }}
      >
        <div className={styles.field}>
          <label htmlFor="api-key-scopes">Scopes</label>
          <input
            aria-describedby="api-key-scopes-hint"
            autoComplete="off"
            className={styles.control}
            id="api-key-scopes"
            name="scopes"
            placeholder="assets.read members.manage"
            required
            type="text"
            value={scopes}
            onChange={(event) => setScopes(event.target.value)}
          />
          <p className={styles.hint} id="api-key-scopes-hint">
            A key needs at least one scope. Separate several with spaces.
          </p>
        </div>
        <button className={styles.primaryButton} disabled={busy} type="submit">
          Create API key
        </button>
      </form>
    </section>
  );
}
