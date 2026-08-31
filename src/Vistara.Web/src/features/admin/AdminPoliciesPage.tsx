import { useCallback } from 'react';
import type { Capabilities, PlatformApiClient } from '../../api/platform';
import { useRemoteResource } from '../../app/useRemoteResource';
import {
  AdminFailure,
  AdminLoading,
  AdminPage,
  AdminPendingContract,
} from './AdminPage';
import { formatBytes } from './format';
import styles from './admin.module.css';

export type AdminPoliciesClient = Pick<PlatformApiClient, 'getCapabilities'>;

interface AdminPoliciesPageProps {
  readonly client: AdminPoliciesClient;
}

export function AdminPoliciesPage({ client }: AdminPoliciesPageProps) {
  const load = useCallback(
    () => client.getCapabilities(),
    [client],
  );
  const { state, reload } = useRemoteResource<Capabilities>(load);

  return (
    <AdminPage
      title="Policies"
      description="The limits this deployment enforces right now. They come from the server configuration, so they are read-only here."
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
          description="The capability document could not be read, so no limits can be shown."
          onRetry={reload}
        />
      ) : null}

      {state.kind === 'ready' ? (
        <dl className={styles.summary}>
          <div>
            <dt>Largest upload</dt>
            <dd>{formatBytes(state.value.upload.maxBytes)}</dd>
          </div>
          <div>
            <dt>Concurrent uploads</dt>
            <dd>
              {state.value.upload.concurrencyUnlimited
                ? 'Unlimited'
                : state.value.upload.maxConcurrentUploads}
            </dd>
          </div>
          <div>
            <dt>Transform deadline</dt>
            <dd>{state.value.imaging.processingDeadlineSeconds}s</dd>
          </div>
          <div>
            <dt>Largest page</dt>
            <dd>{state.value.api.maxPageSize}</dd>
          </div>
          <div>
            <dt>Proxy upload limit</dt>
            <dd>{formatBytes(state.value.api.maxProxyUploadBytes)}</dd>
          </div>
          <div>
            <dt>Full-text search</dt>
            <dd>{state.value.search.text ? 'Enabled' : 'Disabled'}</dd>
          </div>
        </dl>
      ) : null}

      <AdminPendingContract
        title="Retention, sharing, and quota policy"
        description="Trash retention, public link rules, and tenant quotas are not part of the capability document and cannot be edited from the gallery yet."
        contract={
          'GET /api/v1/admin/policies → { retention, sharing, quotas, version } with ETag "v{version}"; PATCH /api/v1/admin/policies with If-Match → 200, 412 stale, 409 conflict'
        }
      />
    </AdminPage>
  );
}
