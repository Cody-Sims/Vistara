import { useCallback, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type {
  AdminJob,
  AdminJobPage,
  JobState,
  PlatformApiClient,
} from '../../api/platform';
import {
  AdminEmpty,
  AdminFailure,
  AdminLoading,
  AdminPage,
} from './AdminPage';
import { formatMoment } from './format';
import { useAdminResource } from './useAdminResource';
import styles from './admin.module.css';

export type AdminJobsClient = Pick<
  PlatformApiClient,
  'listAdminJobs' | 'retryJob' | 'cancelJob'
>;

interface AdminJobsPageProps {
  readonly client: AdminJobsClient;
}

const filters: readonly { value: string; label: string }[] = [
  { value: '', label: 'All jobs' },
  { value: 'queued', label: 'Queued' },
  { value: 'running', label: 'Running' },
  { value: 'failed', label: 'Failed' },
  { value: 'dead', label: 'Needs attention' },
];

const jobStates = new Set<JobState>([
  'queued',
  'running',
  'succeeded',
  'failed',
  'dead',
  'cancelled',
]);

const stateLabels: Record<JobState, string> = {
  queued: 'Queued',
  running: 'Running',
  succeeded: 'Succeeded',
  failed: 'Failed',
  dead: 'Needs attention',
  cancelled: 'Cancelled',
};

export function AdminJobsPage({ client }: AdminJobsPageProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  const rawState = searchParams.get('state') ?? '';
  const selected = jobStates.has(rawState as JobState)
    ? (rawState as JobState)
    : '';

  const load = useCallback(
    () =>
      client.listAdminJobs({
        limit: 50,
        ...(selected ? { states: [selected] } : {}),
      }),
    [client, selected],
  );
  const { state, reload, refresh } = useAdminResource<AdminJobPage>(load);
  const [pending, setPending] = useState<string>();
  const [failure, setFailure] = useState('');
  const [confirmation, setConfirmation] = useState('');

  async function act(job: AdminJob, action: 'retry' | 'cancel') {
    setPending(job.id);
    setFailure('');
    setConfirmation('');

    try {
      if (action === 'retry') {
        await client.retryJob(job.id);
        setConfirmation(`The ${job.kind} job was queued again.`);
      } else {
        await client.cancelJob(job.id);
        setConfirmation(`The ${job.kind} job was cancelled.`);
      }

      await refresh();
    } catch {
      setFailure(
        action === 'retry'
          ? `The ${job.kind} job could not be retried. The queue is unchanged.`
          : `The ${job.kind} job could not be cancelled. The queue is unchanged.`,
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
                <span className={styles.primaryCell}>{job.kind}</span>
                <span className={styles.badge} data-status={job.state}>
                  {stateLabels[job.state]}
                </span>
                <span className={styles.secondaryCell}>
                  Queued {formatMoment(job.queuedAt)}
                  {job.attempts === undefined
                    ? ''
                    : ` · attempt ${job.attempts}${
                        job.maxAttempts ? ` of ${job.maxAttempts}` : ''
                      }`}
                </span>
                {job.lastError ? (
                  <span className={styles.rowError}>{job.lastError}</span>
                ) : null}
              </div>
              <div className={styles.rowActions}>
                {job.state === 'failed' || job.state === 'dead' ? (
                  <button
                    aria-label={`Retry ${job.kind} job`}
                    className={styles.secondaryButton}
                    disabled={pending === job.id}
                    type="button"
                    onClick={() => void act(job, 'retry')}
                  >
                    {pending === job.id ? 'Working…' : 'Retry'}
                  </button>
                ) : null}
                {job.state === 'queued' || job.state === 'running' ? (
                  <button
                    aria-label={`Cancel ${job.kind} job`}
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
    </AdminPage>
  );
}
