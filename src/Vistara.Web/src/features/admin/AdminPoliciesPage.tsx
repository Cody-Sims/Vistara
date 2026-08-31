import { useCallback } from 'react';
import type { PlatformApiClient, TenantPolicies } from '../../api/platform';
import { useRemoteResource } from '../../app/useRemoteResource';
import {
  AdminFailure,
  AdminLoading,
  AdminPage,
  AdminPendingContract,
} from './AdminPage';
import { formatBytes } from './format';
import styles from './admin.module.css';

export type AdminPoliciesClient = Pick<PlatformApiClient, 'getPolicies'>;

interface AdminPoliciesPageProps {
  readonly client: AdminPoliciesClient;
}

/** A `null` quota means no limit at all, which is not the same as zero. */
function describeQuota(
  value: number | null,
  format: (value: number) => string,
): string {
  return value === null ? 'Unlimited' : format(value);
}

export function AdminPoliciesPage({ client }: AdminPoliciesPageProps) {
  const load = useCallback(() => client.getPolicies(), [client]);
  const { state, reload } = useRemoteResource<TenantPolicies>(load);

  return (
    <AdminPage
      title="Policies"
      description="The retention, sharing, and quota limits this workspace enforces. A quota with no limit is shown as unlimited rather than as zero."
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Loading policies…' : ''}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading policies…" shape="card" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Policies are unavailable"
          description="The policy could not be read. Enforcement on the server is unaffected."
          onRetry={reload}
        />
      ) : null}

      {state.kind === 'ready' ? (
        <dl className={styles.summary}>
          <div>
            <dt>Trash retention</dt>
            <dd>{state.value.retention.trashRetentionDays} days</dd>
          </div>
          <div>
            <dt>Purge grace</dt>
            <dd>{state.value.retention.purgeGraceDays} days</dd>
          </div>
          <div>
            <dt>Public links</dt>
            <dd>
              {state.value.sharing.publicLinksEnabled ? 'Allowed' : 'Blocked'}
            </dd>
          </div>
          <div>
            <dt>Longest link lifetime</dt>
            <dd>{state.value.sharing.maxLinkLifetimeDays} days</dd>
          </div>
          <div>
            <dt>Password on public links</dt>
            <dd>
              {state.value.sharing.requirePasswordForPublicLinks
                ? 'Required'
                : 'Optional'}
            </dd>
          </div>
          <div>
            <dt>Storage quota</dt>
            <dd>{describeQuota(state.value.quotas.storageBytes, formatBytes)}</dd>
          </div>
          <div>
            <dt>Daily transform pixels</dt>
            <dd>
              {describeQuota(state.value.quotas.dailyTransformPixels, (value) =>
                new Intl.NumberFormat().format(value),
              )}
            </dd>
          </div>
          <div>
            <dt>Concurrent uploads</dt>
            <dd>
              {describeQuota(state.value.quotas.concurrentUploads, String)}
            </dd>
          </div>
        </dl>
      ) : null}

      <AdminPendingContract
        title="Editing policy from the gallery"
        description="Reading is wired to the published route. Editing needs the same screen to send a merge patch with the version it read, which is the next piece of work here."
        contract={
          'PATCH /api/v1/admin/policies — If-Match: "v{version}", merge patch of retention, sharing, and quotas; an absent quota is unchanged and an explicit null clears the limit'
        }
      />
    </AdminPage>
  );
}
