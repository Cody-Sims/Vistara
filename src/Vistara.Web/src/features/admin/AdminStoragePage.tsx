import { useCallback } from 'react';
import type { PlatformApiClient, StorageOverview } from '../../api/platform';
import {
  AdminEmpty,
  AdminFailure,
  AdminLoading,
  AdminPage,
} from './AdminPage';
import { formatBytes, formatMoment } from './format';
import { useAdminResource } from './useAdminResource';
import styles from './admin.module.css';

export type AdminStorageClient = Pick<PlatformApiClient, 'getStorageOverview'>;

interface AdminStoragePageProps {
  readonly client: AdminStorageClient;
}

const statusLabels = {
  healthy: 'Healthy',
  degraded: 'Degraded',
  unavailable: 'Unavailable',
} as const;

const kindLabels = {
  filesystem: 'Local filesystem',
  s3: 'S3-compatible',
  azure: 'Azure Blob Storage',
  gcs: 'Google Cloud Storage',
} as const;

export function AdminStoragePage({ client }: AdminStoragePageProps) {
  const load = useCallback(() => client.getStorageOverview(), [client]);
  const { state, reload } = useAdminResource<StorageOverview>(load);

  return (
    <AdminPage
      title="Storage"
      description="Where originals, derivatives, and in-flight uploads live, and how much room is left."
      toolbar={
        <button
          className={styles.secondaryButton}
          type="button"
          onClick={reload}
        >
          Refresh
        </button>
      }
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Loading storage usage…' : ''}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading storage usage…" shape="card" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Storage is unavailable"
          description="Usage could not be read. Stored images are unaffected."
          onRetry={reload}
        />
      ) : null}

      {state.kind === 'ready' ? (
        <>
          <dl className={styles.summary}>
            <div>
              <dt>Originals</dt>
              <dd>{formatBytes(state.value.originalBytes)}</dd>
            </div>
            <div>
              <dt>Derivatives</dt>
              <dd>{formatBytes(state.value.derivativeBytes)}</dd>
            </div>
            <div>
              <dt>Staging</dt>
              <dd>{formatBytes(state.value.stagingBytes)}</dd>
            </div>
            {state.value.quotaBytes ? (
              <div>
                <dt>Quota</dt>
                <dd>{formatBytes(state.value.quotaBytes)}</dd>
              </div>
            ) : null}
          </dl>

          {state.value.buckets.length === 0 ? (
            <AdminEmpty>No storage buckets are configured yet.</AdminEmpty>
          ) : (
            <ul className={styles.cards} aria-label="Storage buckets">
              {state.value.buckets.map((bucket) => {
                const share = bucket.quotaBytes
                  ? Math.min(
                      100,
                      Math.round((bucket.usedBytes / bucket.quotaBytes) * 100),
                    )
                  : undefined;

                return (
                  <li className={styles.card} key={bucket.id}>
                    <div className={styles.cardHeader}>
                      <h2>{bucket.id}</h2>
                      <span
                        className={styles.badge}
                        data-status={bucket.status}
                      >
                        {statusLabels[bucket.status]}
                      </span>
                    </div>
                    <p className={styles.cardMeta}>{kindLabels[bucket.kind]}</p>
                    <p className={styles.cardFigure}>
                      {formatBytes(bucket.usedBytes)}
                      {bucket.quotaBytes ? (
                        <span className={styles.cardMeta}>
                          {' '}
                          of {formatBytes(bucket.quotaBytes)}
                        </span>
                      ) : null}
                    </p>
                    {share === undefined ? null : (
                      <div
                        className={styles.meter}
                        role="meter"
                        aria-valuemin={0}
                        aria-valuemax={100}
                        aria-valuenow={share}
                        aria-label={`${bucket.id} quota used`}
                      >
                        <span style={{ inlineSize: `${share}%` }} />
                      </div>
                    )}
                    <p className={styles.cardMeta}>
                      {new Intl.NumberFormat().format(bucket.objectCount)}{' '}
                      objects · checked {formatMoment(bucket.lastCheckedAt)}
                    </p>
                    {bucket.message ? (
                      <p className={styles.cardNotice}>{bucket.message}</p>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          )}
        </>
      ) : null}
    </AdminPage>
  );
}
