import { useCallback, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type {
  JobCollection,
  JobState,
  JobStatus,
  PlatformApiClient,
} from '../../api/platform';
import { isStaleVersion, versionTag } from '../../api/versionTag';
import { useRemoteResource } from '../../app/useRemoteResource';
import {
  AdminEmpty,
  AdminFailure,
  AdminLoading,
  AdminPage,
} from './AdminPage';
import { formatMoment } from './format';
import styles from './admin.module.css';

export type AdminJobsClient = Pick<
  PlatformApiClient,
  'listJobs' | 'retryJob' | 'cancelJob'
>;

interface AdminJobsPageProps {
  readonly client: AdminJobsClient;
}

const jobStates: readonly JobState[] = [
  'Pending',
  'Leased',
  'RetryScheduled',
  'Completed',
  'DeadLettered',
];

const stateLabels: Record<JobState, string> = {
  Pending: 'Queued',
  Leased: 'Running',
  RetryScheduled: 'Retry scheduled',
  Completed: 'Completed',
  DeadLettered: 'Needs attention',
};

const filters: readonly { value: string; label: string }[] = [
  { value: '', label: 'All jobs' },
  ...jobStates.map((state) => ({ value: state, label: stateLabels[state] })),
];

export function AdminJobsPage({ client }: AdminJobsPageProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  const raw = searchParams.get('state') ?? '';
  const selected = jobStates.includes(raw as JobState) ? (raw as JobState) : '';

  const load = useCallback(
    () =>
      client.listJobs({
        limit: 50,
        ...(selected ? { states: [selected] } : {}),
      }),
    [client, selected],
  );
  const { state, reload, refresh } = useRemoteResource<JobCollection>(load);
  const [pending, setPending] = useState<string>();
  const [failure, setFailure] = useState('');
  const [confirmation, setConfirmation] = useState('');

  async function act(job: JobStatus, action: 'retry' | 'cancel') {
    setPending(job.id);
    setFailure('');
    setConfirmation('');

    try {
      const options = { ifMatch: versionTag(job.version) };
      if (action === 'retry') {
        await client.retryJob(job.id, options);
        setConfirmation(`The ${job.type} job was queued again.`);
      } else {
        await client.cancelJob(job.id, options);
        setConfirmation(`The ${job.type} job was cancelled.`);
      }

      await refresh();
    } catch (error) {
      setFailure(
        isStaleVersion(error)
          ? `The ${job.type} job changed somewhere else. Refresh the queue and try again.`
          : action === 'retry'
            ? `The ${job.type} job could not be retried. The queue is unchanged.`
            : `The ${job.type} job could not be cancelled. The queue is unchanged.`,
      );
    } finally {
      setPending(undefined);
    }
  }

  return (
    <AdminPage
      title="Jobs"
      description="Background work for derivatives, purges, and maintenance. Failures stay listed until they are retried or cancelled."
      toolbar={
        <>
          <div className={styles.filterField}>
            <label htmlFor="jobs-state">Show jobs</label>
            <select
              className={styles.control}
              id="jobs-state"
              value={selected}
              onChange={(event) => {
                const next = new URLSearchParams(searchParams);
                if (event.target.value) {
                  next.set('state', event.target.value);
                } else {
                  next.delete('state');
                }
                setSearchParams(next);
              }}
            >
              {filters.map((filter) => (
                <option key={filter.value} value={filter.value}>
                  {filter.label}
                </option>
              ))}
            </select>
          </div>
          <button
            className={styles.secondaryButton}
            type="button"
            onClick={reload}
          >
            Refresh
          </button>
        </>
      }
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Loading jobs…' : confirmation}
      </p>

      {state.kind === 'loading' ? <AdminLoading label="Loading jobs…" /> : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Jobs are unavailable"
          description="The queue could not be read. Background work continues on the server."
          onRetry={reload}
        />
      ) : null}

      {failure ? (
        <p className={styles.alert} role="alert">
          {failure}
        </p>
      ) : null}

      {state.kind === 'ready' && state.value.items.length === 0 ? (
        <AdminEmpty>No jobs match this filter right now.</AdminEmpty>
      ) : null}

      {state.kind === 'ready' && state.value.items.length > 0 ? (
        <ul className={styles.rows} aria-label="Background jobs">
          {state.value.items.map((job) => (
            <li className={styles.row} key={job.id}>
              <div className={styles.rowMain}>
                <span className={styles.primaryCell}>{job.type}</span>
                <span className={styles.badge} data-status={job.state}>
                  {stateLabels[job.state as JobState] ?? job.state}
                </span>
                <span className={styles.secondaryCell}>
                  Created {formatMoment(job.createdAt)} · attempt {job.attempts}{' '}
                  of {job.maxAttempts} · next attempt{' '}
                  {formatMoment(job.availableAt)}
                </span>
                {job.failure ? (
                  <span className={styles.rowError}>
                    <code>{job.failure.code}</code>
                    <span>{job.failure.summary}</span>
                  </span>
                ) : null}
              </div>
              <div className={styles.rowActions}>
                {job.state === 'DeadLettered' ||
                job.state === 'RetryScheduled' ? (
                  <button
                    aria-label={`Retry ${job.type} job`}
                    className={styles.secondaryButton}
                    disabled={pending === job.id}
                    type="button"
                    onClick={() => void act(job, 'retry')}
                  >
                    {pending === job.id ? 'Working…' : 'Retry'}
                  </button>
                ) : null}
                {job.state === 'Pending' || job.state === 'Leased' ? (
                  <button
                    aria-label={`Cancel ${job.type} job`}
                    className={styles.secondaryButton}
                    disabled={pending === job.id}
                    type="button"
                    onClick={() => void act(job, 'cancel')}
                  >
                    {pending === job.id ? 'Working…' : 'Cancel'}
                  </button>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      ) : null}

      {state.kind === 'ready' && state.value.nextCursor ? (
        <p className={styles.hintRow}>
          More jobs are available. Narrow the filter to reach them.
        </p>
      ) : null}
    </AdminPage>
  );
}
