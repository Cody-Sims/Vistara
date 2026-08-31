import { useEffect, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { VistaraApiError } from '../../api/generated/client';
import type { JobStatus, PlatformApiClient } from '../../api/platform';
import { AdminPage, AdminPendingContract } from './AdminPage';
import { formatMoment } from './format';
import styles from './admin.module.css';

export type AdminJobsClient = Pick<PlatformApiClient, 'getJob'>;

interface AdminJobsPageProps {
  readonly client: AdminJobsClient;
}

type LookupState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly job: JobStatus }
  | { readonly kind: 'failed'; readonly message: string };

async function runLookup(
  client: AdminJobsClient,
  jobId: string,
): Promise<LookupState> {
  try {
    return { kind: 'ready', job: await client.getJob(jobId) };
  } catch (error) {
    return {
      kind: 'failed',
      message:
        error instanceof VistaraApiError && error.status === 404
          ? 'No job with that identifier is visible to this workspace.'
          : 'The job could not be read. Background work continues on the server.',
    };
  }
}

export function AdminJobsPage({ client }: AdminJobsPageProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  const requested = (searchParams.get('job') ?? '').trim();
  const [draft, setDraft] = useState(requested);
  const [tracked, setTracked] = useState(requested);
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<LookupState>(() =>
    requested ? { kind: 'loading' } : { kind: 'idle' },
  );

  if (tracked !== requested) {
    setTracked(requested);
    setDraft(requested);
    setState(requested ? { kind: 'loading' } : { kind: 'idle' });
  }

  useEffect(() => {
    if (!requested) {
      return;
    }

    let active = true;
    void runLookup(client, requested).then((next) => {
      if (active) {
        setState(next);
      }
    });

    return () => {
      active = false;
    };
  }, [attempt, client, requested]);

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const jobId = draft.trim();
    const next = new URLSearchParams(searchParams);
    if (jobId) {
      next.set('job', jobId);
    } else {
      next.delete('job');
    }

    setSearchParams(next);
    if (jobId && jobId === requested) {
      setState({ kind: 'loading' });
      setAttempt((value) => value + 1);
    }
  }

  return (
    <AdminPage
      title="Jobs"
      description="Read the state of a background job by its identifier. Job identifiers appear in upload results, share operations, and problem details."
    >
      <form className={styles.form} onSubmit={submit}>
        <fieldset className={styles.fieldset}>
          <legend>Job lookup</legend>
          <div className={styles.field}>
            <label htmlFor="job-id">Job identifier</label>
            <input
              autoComplete="off"
              className={styles.control}
              id="job-id"
              name="job"
              type="search"
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
            />
          </div>
          <div className={styles.formActions}>
            <button className={styles.primaryButton} type="submit">
              Look up job
            </button>
          </div>
        </fieldset>
      </form>

      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Reading the job…' : ''}
      </p>

      {state.kind === 'failed' ? (
        <p className={styles.alert} role="alert">
          {state.message}
        </p>
      ) : null}

      {state.kind === 'ready' ? (
        <div className={styles.row}>
          <div className={styles.rowMain}>
            <span className={styles.primaryCell}>{state.job.type}</span>
            <span className={styles.badge} data-status={state.job.state}>
              {state.job.state}
            </span>
            <span className={styles.secondaryCell}>
              Created {formatMoment(state.job.createdAt)} · attempt{' '}
              {state.job.attempts} of {state.job.maxAttempts} · next attempt{' '}
              {formatMoment(state.job.availableAt)}
            </span>
            {state.job.completedAt ? (
              <span className={styles.secondaryCell}>
                Completed {formatMoment(state.job.completedAt)}
              </span>
            ) : null}
            {state.job.failure ? (
              <span className={styles.rowError}>
                <code>{state.job.failure.code}</code>
                <span>{state.job.failure.summary}</span>
              </span>
            ) : null}
          </div>
        </div>
      ) : null}

      <AdminPendingContract
        title="Queue view, retry, and cancel"
        description="One job can be read by identifier. Listing the queue and acting on a job need a collection and two versioned operator routes."
        contract={
          'GET /api/v1/jobs?states=Failed&states=Dead&type=…&limit=…&cursor=… → { items: JobStatus[], nextCursor? }; POST /api/v1/jobs/{jobId}/retry and /cancel with If-Match: "v{version}"'
        }
      />
    </AdminPage>
  );
}
