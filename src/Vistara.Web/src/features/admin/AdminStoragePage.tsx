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

export type AdminStorageClient = Pick<PlatformApiClient, 'getCapabilities'>;

interface AdminStoragePageProps {
  readonly client: AdminStorageClient;
}

export function AdminStoragePage({ client }: AdminStoragePageProps) {
  const load = useCallback(
    () => client.getCapabilities(),
    [client],
  );
  const { state, reload } = useRemoteResource<Capabilities>(load);

  return (
    <AdminPage
      title="Storage"
      description="How this deployment stores originals and derivatives, and the limits it enforces on every upload."
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
        {state.kind === 'loading' ? 'Loading storage configuration…' : ''}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading storage configuration…" shape="card" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Storage is unavailable"
          description="The capability document could not be read. Stored images are unaffected."
          onRetry={reload}
        />
      ) : null}

      {state.kind === 'ready' ? (
        <>
          <dl className={styles.summary}>
            <div>
              <dt>Storage provider</dt>
              <dd>{state.value.storage.provider}</dd>
            </div>
            <div>
              <dt>Database</dt>
              <dd>{state.value.database.provider}</dd>
            </div>
            <div>
              <dt>Largest object</dt>
              <dd>{formatBytes(state.value.storage.maxObjectBytes)}</dd>
            </div>
            <div>
              <dt>Largest upload</dt>
              <dd>{formatBytes(state.value.upload.maxBytes)}</dd>
            </div>
          </dl>

          <ul className={styles.cards} aria-label="Storage behaviour">
            <li className={styles.card}>
              <div className={styles.cardHeader}>
                <h2>Transfer</h2>
              </div>
              <p className={styles.cardMeta}>
                Direct uploads {state.value.storage.directUpload ? 'on' : 'off'}{' '}
                · multipart{' '}
                {state.value.storage.multipartUpload ? 'on' : 'off'} · range
                reads {state.value.storage.rangeReads ? 'on' : 'off'}
              </p>
              <p className={styles.cardMeta}>
                Multipart parts between{' '}
                {formatBytes(state.value.storage.minMultipartPartBytes)} and{' '}
                {formatBytes(state.value.storage.maxMultipartPartBytes)}, up to{' '}
                {new Intl.NumberFormat().format(
                  state.value.storage.maxMultipartParts,
                )}{' '}
                parts
              </p>
            </li>
            <li className={styles.card}>
              <div className={styles.cardHeader}>
                <h2>Imaging</h2>
              </div>
              <p className={styles.cardMeta}>
                {state.value.imaging.provider} · accepts{' '}
                {state.value.imaging.inputFormats.join(', ')} · writes{' '}
                {state.value.imaging.outputFormats.join(', ')}
              </p>
              <p className={styles.cardMeta}>
                Up to {state.value.imaging.maxWidth}×
                {state.value.imaging.maxHeight} and{' '}
                {formatBytes(state.value.imaging.maxEncodedBytes)} encoded
              </p>
            </li>
            <li className={styles.card}>
              <div className={styles.cardHeader}>
                <h2>Concurrency</h2>
              </div>
              <p className={styles.cardMeta}>
                {state.value.upload.concurrencyUnlimited
                  ? 'Unlimited concurrent uploads'
                  : `${state.value.upload.maxConcurrentUploads} concurrent uploads`}{' '}
                · {state.value.imaging.maxConcurrentTransforms} concurrent
                transforms
              </p>
              <p className={styles.cardMeta}>
                Multipart above{' '}
                {formatBytes(state.value.upload.multipartThresholdBytes)}
              </p>
            </li>
          </ul>
        </>
      ) : null}

      <AdminPendingContract
        title="Consumption and bucket health"
        description="The capability document describes configured limits, not how much room is left. Usage, object counts, and bucket health need their own route."
        contract={
          'GET /api/v1/admin/storage → { buckets: [{ id, kind, status, usedBytes, quotaBytes, objectCount, lastCheckedAt, message }], originalBytes, derivativeBytes, stagingBytes, quotaBytes, pendingUploadBytes }'
        }
      />
    </AdminPage>
  );
}
