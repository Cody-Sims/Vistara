import { useCallback, useState } from 'react';
import { VistaraApiError } from '../../api/generated/client';
import type { PolicySettings, PlatformApiClient } from '../../api/platform';
import { AdminFailure, AdminLoading, AdminPage } from './AdminPage';
import { useAdminResource } from './useAdminResource';
import styles from './admin.module.css';

export type AdminPoliciesClient = Pick<
  PlatformApiClient,
  'getPolicies' | 'updatePolicies'
>;

interface AdminPoliciesPageProps {
  readonly client: AdminPoliciesClient;
}

interface PolicyDraft {
  trashRetentionDays: number;
  purgeGraceDays: number;
  publicLinksEnabled: boolean;
  maxLinkLifetimeDays: number;
  requirePasswordForPublicLinks: boolean;
  concurrentUploads: number;
}

type SaveState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'saved' }
  | { readonly kind: 'conflict' }
  | { readonly kind: 'failed' };

function toDraft(policies: PolicySettings): PolicyDraft {
  return {
    trashRetentionDays: policies.retention.trashRetentionDays ?? 30,
    purgeGraceDays: policies.retention.purgeGraceDays ?? 7,
    publicLinksEnabled: policies.sharing.publicLinksEnabled ?? true,
    maxLinkLifetimeDays: policies.sharing.maxLinkLifetimeDays ?? 30,
    requirePasswordForPublicLinks:
      policies.sharing.requirePasswordForPublicLinks ?? false,
    concurrentUploads: policies.quotas.concurrentUploads ?? 4,
  };
}

export function AdminPoliciesPage({ client }: AdminPoliciesPageProps) {
  const load = useCallback(() => client.getPolicies(), [client]);
  const { state, reload, refresh } = useAdminResource<PolicySettings>(load);
  const [draft, setDraft] = useState<PolicyDraft>();
  const [loadedVersion, setLoadedVersion] = useState<number>();
  const [save, setSave] = useState<SaveState>({ kind: 'idle' });

  if (state.kind === 'ready' && loadedVersion !== state.value.version) {
    setLoadedVersion(state.value.version);
    setDraft(toDraft(state.value));
  }

  async function submit() {
    if (!draft || state.kind !== 'ready') {
      return;
    }

    setSave({ kind: 'saving' });
    try {
      await client.updatePolicies(
        {
          retention: {
            trashRetentionDays: draft.trashRetentionDays,
            purgeGraceDays: draft.purgeGraceDays,
          },
          sharing: {
            publicLinksEnabled: draft.publicLinksEnabled,
            maxLinkLifetimeDays: draft.maxLinkLifetimeDays,
            requirePasswordForPublicLinks: draft.requirePasswordForPublicLinks,
          },
          quotas: { concurrentUploads: draft.concurrentUploads },
        },
        { ifMatch: state.etag ?? `"${state.value.version}"` },
      );
      await refresh();
      setSave({ kind: 'saved' });
    } catch (error) {
      setSave(
        error instanceof VistaraApiError && error.status === 409
          ? { kind: 'conflict' }
          : { kind: 'failed' },
      );
    }
  }

  function update<K extends keyof PolicyDraft>(key: K, value: PolicyDraft[K]) {
    setSave({ kind: 'idle' });
    setDraft((current) => (current ? { ...current, [key]: value } : current));
  }

  return (
    <AdminPage
      title="Policies"
      description="Retention, sharing, and upload limits for this workspace. Saving requires the version you loaded, so a change made elsewhere is never silently replaced."
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading'
          ? 'Loading policies…'
          : save.kind === 'saved'
            ? 'Policies saved.'
            : ''}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading policies…" shape="card" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Policies are unavailable"
          description="The current policy could not be read, so nothing can be edited yet."
          onRetry={reload}
        />
      ) : null}

      {save.kind === 'conflict' ? (
        <p className={styles.alert} role="alert">
          These policies changed somewhere else while you were editing. Reload
          them and reapply your change so nothing is overwritten.
        </p>
      ) : null}

      {save.kind === 'failed' ? (
        <p className={styles.alert} role="alert">
          Policies could not be saved. Nothing was changed; try again.
        </p>
      ) : null}

      {state.kind === 'ready' && draft ? (
        <form
          className={styles.form}
          onSubmit={(event) => {
            event.preventDefault();
            void submit();
          }}
        >
          <fieldset className={styles.fieldset}>
            <legend>Retention</legend>
            <div className={styles.field}>
              <label htmlFor="policy-trash">Trash retention (days)</label>
              <input
                className={styles.control}
                id="policy-trash"
                min={1}
                max={365}
                type="number"
                value={draft.trashRetentionDays}
                onChange={(event) =>
                  update('trashRetentionDays', Number(event.target.value))
                }
              />
              <p className={styles.fieldHint}>
                How long trashed images stay restorable before purging begins.
              </p>
            </div>
            <div className={styles.field}>
              <label htmlFor="policy-purge">Purge grace (days)</label>
              <input
                className={styles.control}
                id="policy-purge"
                min={0}
                max={90}
                type="number"
                value={draft.purgeGraceDays}
                onChange={(event) =>
                  update('purgeGraceDays', Number(event.target.value))
                }
              />
            </div>
          </fieldset>

          <fieldset className={styles.fieldset}>
            <legend>Sharing</legend>
            <label className={styles.toggle}>
              <input
                aria-label="Allow public links"
                checked={draft.publicLinksEnabled}
                type="checkbox"
                onChange={(event) =>
                  update('publicLinksEnabled', event.target.checked)
                }
              />
              Allow public links
            </label>
            <label className={styles.toggle}>
              <input
                aria-label="Require a password on public links"
                checked={draft.requirePasswordForPublicLinks}
                type="checkbox"
                onChange={(event) =>
                  update(
                    'requirePasswordForPublicLinks',
                    event.target.checked,
                  )
                }
              />
              Require a password on public links
            </label>
            <div className={styles.field}>
              <label htmlFor="policy-lifetime">
                Longest link lifetime (days)
              </label>
              <input
                className={styles.control}
                disabled={!draft.publicLinksEnabled}
                id="policy-lifetime"
                min={1}
                max={365}
                type="number"
                value={draft.maxLinkLifetimeDays}
                onChange={(event) =>
                  update('maxLinkLifetimeDays', Number(event.target.value))
                }
              />
            </div>
          </fieldset>

          <fieldset className={styles.fieldset}>
            <legend>Uploads</legend>
            <div className={styles.field}>
              <label htmlFor="policy-uploads">Concurrent uploads</label>
              <input
                className={styles.control}
                id="policy-uploads"
                min={1}
                max={32}
                type="number"
                value={draft.concurrentUploads}
                onChange={(event) =>
                  update('concurrentUploads', Number(event.target.value))
                }
              />
            </div>
          </fieldset>

          <div className={styles.formActions}>
            <button
              className={styles.primaryButton}
              disabled={save.kind === 'saving'}
              type="submit"
            >
              {save.kind === 'saving' ? 'Saving…' : 'Save policies'}
            </button>
            {save.kind === 'conflict' ? (
              <button
                className={styles.secondaryButton}
                type="button"
                onClick={() => {
                  setSave({ kind: 'idle' });
                  setLoadedVersion(undefined);
                  reload();
                }}
              >
                Reload policies
              </button>
            ) : null}
          </div>
        </form>
      ) : null}
    </AdminPage>
  );
}
