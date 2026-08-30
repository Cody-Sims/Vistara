import { useEffect, useId, useMemo, useRef, useState } from 'react';
import {
  VistaraApiError,
  type VistaraApiClient,
} from '../../api/generated';
import type {
  PurgeBatch,
  PurgeDryRun,
  TrashAsset,
  VersionedAssetReference,
} from '../../api/generated/models';
import styles from './Trash.module.css';

export interface TrashManagerProps {
  readonly client: Pick<
    VistaraApiClient,
    | 'listTrash'
    | 'restoreAssets'
    | 'dryRunPurge'
    | 'confirmPurge'
    | 'getPurgeStatus'
  >;
  readonly now?: () => Date;
  readonly createIdempotencyKey?: () => string;
  readonly reauthenticate: () => Promise<boolean>;
}

const defaultNow = () => new Date();
const defaultIdempotencyKey = () => crypto.randomUUID();

export function TrashManager({
  client,
  now = defaultNow,
  createIdempotencyKey = defaultIdempotencyKey,
  reauthenticate,
}: TrashManagerProps) {
  const headingId = useId();
  const confirmInputRef = useRef<HTMLInputElement>(null);
  const purgeTriggerRef = useRef<HTMLButtonElement>(null);
  const confirmDialogRef = useRef<HTMLDivElement>(null);
  const [items, setItems] = useState<readonly TrashAsset[]>([]);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<string>();
  const [dryRun, setDryRun] = useState<PurgeDryRun>();
  const [batch, setBatch] = useState<PurgeBatch>();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmation, setConfirmation] = useState('');

  const selectedItems = useMemo(
    () => items.filter((item) => selected.has(item.asset.id)),
    [items, selected],
  );

  async function loadTrash() {
    setLoading(true);
    try {
      const response = await client.listTrash();
      setItems(response.data.items);
      setSelected(new Set());
      setError(undefined);
    } catch (caught) {
      setError(messageFor(caught, 'Trash could not be loaded.'));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    let active = true;
    void client
      .listTrash()
      .then((response) => {
        if (active) {
          setItems(response.data.items);
          setError(undefined);
        }
      })
      .catch((caught: unknown) => {
        if (active) {
          setError(messageFor(caught, 'Trash could not be loaded.'));
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });
    return () => {
      active = false;
    };
  }, [client]);

  async function restore(targets: readonly TrashAsset[]) {
    setBusy(true);
    setError(undefined);
    try {
      await client.restoreAssets(
        { items: targets.map(toVersionedReference) },
        { idempotencyKey: createIdempotencyKey() },
      );
      const restoredIds = new Set(targets.map((item) => item.asset.id));
      setItems((current) =>
        current.filter((item) => !restoredIds.has(item.asset.id)),
      );
      setSelected((current) => {
        const next = new Set(current);
        restoredIds.forEach((id) => next.delete(id));
        return next;
      });
      setStatus(
        `Restore queued for ${targets.length} ${
          targets.length === 1 ? 'item' : 'items'
        }.`,
      );
    } catch (caught) {
      if (
        caught instanceof VistaraApiError &&
        (caught.status === 409 || caught.status === 412)
      ) {
        await loadTrash();
        setError(
          'One or more items changed elsewhere. Trash has been refreshed; review your selection.',
        );
      } else {
        setError(messageFor(caught, 'The restore could not be queued.'));
      }
    } finally {
      setBusy(false);
    }
  }

  async function reviewPurge() {
    setBusy(true);
    setError(undefined);
    setDryRun(undefined);
    setBatch(undefined);
    try {
      const response = await client.dryRunPurge(
        {
          phase: 'dryRun',
          items: selectedItems.map(toVersionedReference),
        },
        { idempotencyKey: createIdempotencyKey() },
      );
      setDryRun(response.data);
      setStatus(
        `Dry run complete: ${response.data.eligibleCount} of ${response.data.candidateCount} eligible.`,
      );
    } catch (caught) {
      setError(
        messageFor(caught, 'Permanent deletion impact could not be calculated.'),
      );
    } finally {
      setBusy(false);
    }
  }

  async function beginConfirmation() {
    setBusy(true);
    setError(undefined);
    try {
      const authenticated = await reauthenticate();
      if (!authenticated) {
        setError('Reauthentication was not completed. Nothing was deleted.');
        return;
      }
      setConfirmation('');
      setConfirmOpen(true);
      queueMicrotask(() => confirmInputRef.current?.focus());
    } catch {
      setError('Reauthentication failed. Nothing was deleted.');
    } finally {
      setBusy(false);
    }
  }

  async function confirmPurge() {
    if (!dryRun) {
      return;
    }
    setBusy(true);
    setError(undefined);
    try {
      const response = await client.confirmPurge(
        dryRun.batchId,
        {
          dryRunDigest: dryRun.dryRunDigest,
          acknowledgePermanentDeletion: true,
        },
        {
          idempotencyKey: createIdempotencyKey(),
          ifMatch: `"v${dryRun.version}"`,
        },
      );
      setBatch(response.data);
      setConfirmOpen(false);
      setConfirmation('');
      setStatus(batchStatus(response.data));
      purgeTriggerRef.current?.focus();
    } catch (caught) {
      if (caught instanceof VistaraApiError && caught.status === 412) {
        const response = await client.getPurgeStatus(dryRun.batchId);
        setBatch(response.data);
        setConfirmOpen(false);
        setConfirmation('');
        setError(
          'This purge changed elsewhere. Its latest status has been loaded.',
        );
        setStatus(batchStatus(response.data));
        purgeTriggerRef.current?.focus();
      } else {
        setError(
          messageFor(caught, 'Permanent deletion could not be authorized.'),
        );
      }
    } finally {
      setBusy(false);
    }
  }

  async function refreshBatch() {
    if (!dryRun) {
      return;
    }
    setBusy(true);
    try {
      const response = await client.getPurgeStatus(dryRun.batchId);
      setBatch(response.data);
      setStatus(batchStatus(response.data));
      setError(undefined);
    } catch (caught) {
      setError(messageFor(caught, 'Purge status could not be loaded.'));
    } finally {
      setBusy(false);
    }
  }

  const requiredConfirmation = dryRun
    ? `PURGE ${dryRun.eligibleCount} ITEMS`
    : '';

  return (
    <section className={styles.feature} aria-labelledby={headingId}>
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Recovery</p>
          <h1 id={headingId}>Trash</h1>
          <p>
            Restore items during retention. Permanent deletion is a separate,
            human-authorized operation.
          </p>
        </div>
        <button
          type="button"
          className={styles.secondaryButton}
          disabled={loading}
          onClick={loadTrash}
        >
          Refresh
        </button>
      </header>

      {error ? <p role="alert" className={styles.error}>{error}</p> : null}
      {status ? <p role="status" className={styles.status}>{status}</p> : null}

      <div className={styles.toolbar} aria-label="Trash actions">
        <span aria-live="polite">
          {selected.size} {selected.size === 1 ? 'item' : 'items'} selected
        </span>
        <div className={styles.actions}>
          <button
            type="button"
            className={styles.secondaryButton}
            disabled={busy || selectedItems.length === 0}
            onClick={() => restore(selectedItems)}
          >
            Restore selected
          </button>
          <button
            type="button"
            ref={purgeTriggerRef}
            className={styles.dangerButton}
            disabled={busy || selectedItems.length === 0}
            onClick={reviewPurge}
          >
            {busy ? 'Working…' : 'Review permanent deletion'}
          </button>
        </div>
      </div>

      {loading ? <p role="status">Loading trash…</p> : null}
      {!loading && items.length === 0 ? (
        <p className={styles.empty}>Trash is empty.</p>
      ) : null}
      <div className={styles.cardList}>
        {items.map((item) => {
          const selectedItem = selected.has(item.asset.id);
          const retained = new Date(item.purgeAt) > now();
          return (
            <article
              key={item.asset.id}
              className={styles.card}
              aria-label={item.asset.title}
            >
              <div className={styles.cardHeading}>
                <label className={styles.select}>
                  <input
                    type="checkbox"
                    aria-label={`Select ${item.asset.title}`}
                    checked={selectedItem}
                    onChange={(event) => {
                      setSelected((current) => {
                        const next = new Set(current);
                        if (event.target.checked) {
                          next.add(item.asset.id);
                        } else {
                          next.delete(item.asset.id);
                        }
                        return next;
                      });
                      setDryRun(undefined);
                      setBatch(undefined);
                    }}
                  />
                  <span>{item.asset.title}</span>
                </label>
                <span
                  className={styles.badge}
                  data-retained={retained ? 'true' : 'false'}
                >
                  {retained ? 'In retention' : 'Retention elapsed'}
                </span>
              </div>
              <p className={styles.reason}>Reason: {item.reason}</p>
              <dl className={styles.details}>
                <div>
                  <dt>Deleted</dt>
                  <dd>Deleted {formatDate(new Date(item.deletedAt))}</dd>
                </div>
                <div>
                  <dt>Purge date</dt>
                  <dd>Scheduled purge {formatDate(new Date(item.purgeAt))}</dd>
                </div>
                <div>
                  <dt>Holds</dt>
                  <dd>
                    {item.activeHoldCount}{' '}
                    {item.activeHoldCount === 1 ? 'active hold' : 'active holds'}
                  </dd>
                </div>
                <div>
                  <dt>References</dt>
                  <dd>
                    {item.blockingReferenceCount}{' '}
                    {item.blockingReferenceCount === 1
                      ? 'blocking reference'
                      : 'blocking references'}
                  </dd>
                </div>
                <div>
                  <dt>Potential reclaim</dt>
                  <dd>{formatBytes(item.estimatedReclaimBytes)}</dd>
                </div>
              </dl>
              <button
                type="button"
                className={styles.secondaryButton}
                disabled={busy}
                onClick={() => restore([item])}
              >
                Undo deletion
              </button>
            </article>
          );
        })}
      </div>

      {dryRun ? (
        <section
          className={styles.impact}
          aria-label="Permanent deletion impact"
        >
          <div className={styles.cardHeading}>
            <div>
              <p className={styles.eyebrow}>Dry run only</p>
              <h2>Permanent deletion impact</h2>
            </div>
            <strong>
              {dryRun.eligibleCount} of {dryRun.candidateCount} eligible
            </strong>
          </div>
          <p>
            This review expires {formatDateTime(new Date(dryRun.expiresAt))}.
            Vistara rechecks revisions, holds, references, and permissions at
            confirmation.
          </p>
          <p className={styles.aiBoundary}>
            AI suggestions cannot approve or directly execute permanent
            deletion. A signed-in person must select and confirm eligible
            items.
          </p>
          <ul className={styles.impactList}>
            {dryRun.items.map((item) => (
              <li key={`${item.assetId}:${item.revisionNumber}`}>
                <div className={styles.cardHeading}>
                  <strong>{item.title}</strong>
                  <span>{item.eligible ? 'Eligible' : 'Blocked'}</span>
                </div>
                <p>
                  {item.sharedLinkImpact}{' '}
                  {item.sharedLinkImpact === 1
                    ? 'active share link'
                    : 'active share links'}
                  {' · '}
                  {formatBytes(item.estimatedReclaimBytes)}
                </p>
                {item.barriers.length > 0 ? (
                  <ul className={styles.barriers}>
                    {item.barriers.map((barrier) => (
                      <li key={barrier}>{barrierLabel(barrier)}</li>
                    ))}
                  </ul>
                ) : null}
              </li>
            ))}
          </ul>
          <div className={styles.actions}>
            <button
              type="button"
              className={styles.dangerButton}
              disabled={busy || dryRun.eligibleCount === 0}
              onClick={beginConfirmation}
            >
              Continue to confirmation
            </button>
            <button
              type="button"
              className={styles.secondaryButton}
              onClick={() => {
                setDryRun(undefined);
                setBatch(undefined);
              }}
            >
              Cancel purge
            </button>
          </div>
        </section>
      ) : null}

      {batch ? (
        <section className={styles.batch} aria-labelledby="purge-status-heading">
          <div>
            <h2 id="purge-status-heading">Permanent deletion status</h2>
            <p>
              {titleCase(batch.state)} · {batch.processedCount} of{' '}
              {batch.candidateCount} processed ·{' '}
              {formatBytes(batch.reclaimedBytes)} reclaimed
            </p>
          </div>
          {!['completed', 'cancelled'].includes(batch.state) ? (
            <button
              type="button"
              className={styles.secondaryButton}
              disabled={busy}
              onClick={refreshBatch}
            >
              Refresh status
            </button>
          ) : null}
        </section>
      ) : null}

      {confirmOpen && dryRun ? (
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="confirm-purge-title"
          className={styles.dialogBackdrop}
          onKeyDown={(event) => {
            if (event.key === 'Escape' && !busy) {
              setConfirmOpen(false);
              setConfirmation('');
              purgeTriggerRef.current?.focus();
            } else if (event.key === 'Tab') {
              trapFocus(event, confirmDialogRef.current);
            }
          }}
        >
          <div className={styles.dialog} ref={confirmDialogRef}>
            <p className={styles.eyebrow}>Irreversible action</p>
            <h2 id="confirm-purge-title">Confirm permanent deletion</h2>
            <p>
              {dryRun.eligibleCount}{' '}
              {dryRun.eligibleCount === 1 ? 'item is' : 'items are'} eligible.
              Purged files cannot be restored.
            </p>
            <label className={styles.field}>
              <span>Type {requiredConfirmation} to confirm</span>
              <input
                ref={confirmInputRef}
                value={confirmation}
                autoComplete="off"
                onChange={(event) => setConfirmation(event.target.value)}
              />
            </label>
            <div className={styles.actions}>
              <button
                type="button"
                className={styles.dangerButton}
                disabled={busy || confirmation !== requiredConfirmation}
                onClick={confirmPurge}
              >
                {busy ? 'Authorizing…' : 'Permanently delete'}
              </button>
              <button
                type="button"
                className={styles.secondaryButton}
                disabled={busy}
                onClick={() => {
                  setConfirmOpen(false);
                  setConfirmation('');
                  purgeTriggerRef.current?.focus();
                }}
              >
                Keep items
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </section>
  );
}

function toVersionedReference(item: TrashAsset): VersionedAssetReference {
  return {
    id: item.asset.id,
    version: item.asset.version,
  };
}

function messageFor(caught: unknown, fallback: string) {
  return caught instanceof Error && caught.message ? caught.message : fallback;
}

function formatDate(date: Date) {
  return new Intl.DateTimeFormat('en-US', {
    dateStyle: 'medium',
    timeZone: 'UTC',
  }).format(date);
}

function formatDateTime(date: Date) {
  return new Intl.DateTimeFormat('en-US', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(date);
}

function formatBytes(bytes: number) {
  return new Intl.NumberFormat('en-US', {
    style: 'unit',
    unit: bytes >= 1_048_576 ? 'megabyte' : 'kilobyte',
    unitDisplay: 'short',
    maximumFractionDigits: 1,
  }).format(
    bytes >= 1_048_576 ? bytes / 1_048_576 : Math.max(bytes / 1024, 0),
  );
}

function barrierLabel(
  barrier: PurgeDryRun['items'][number]['barriers'][number],
) {
  const labels = {
    notTrashed: 'Not in trash',
    retentionPeriod: 'Retention period',
    activeHold: 'Active hold',
    revisionChanged: 'Revision changed',
    blockingReference: 'Blocking reference',
  } satisfies Record<typeof barrier, string>;
  return labels[barrier];
}

function batchStatus(batch: PurgeBatch) {
  return `Permanent deletion is ${batch.state}: ${batch.processedCount} of ${batch.candidateCount} processed.`;
}

function titleCase(value: string) {
  return value.replace(/([A-Z])/g, ' $1').replace(/^./, (letter) =>
    letter.toUpperCase(),
  );
}

function trapFocus(event: React.KeyboardEvent, container: HTMLElement | null) {
  if (!container) {
    return;
  }
  const focusable = Array.from(
    container.querySelectorAll<HTMLElement>(
      'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href]',
    ),
  );
  const first = focusable[0];
  const last = focusable.at(-1);
  if (!first || !last) {
    return;
  }
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}
