import { useCallback, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type {
  AuditEvent,
  AuditEventPage,
  AuditOutcome,
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

export type AdminAuditClient = Pick<PlatformApiClient, 'listAuditEvents'>;

interface AdminAuditPageProps {
  readonly client: AdminAuditClient;
}

const outcomes = new Set<AuditOutcome>(['succeeded', 'denied', 'failed']);

const outcomeLabels: Record<AuditOutcome, string> = {
  succeeded: 'Succeeded',
  denied: 'Denied',
  failed: 'Failed',
};

const actorLabels = {
  user: 'Person',
  apiKey: 'API key',
  system: 'System',
} as const;

export function AdminAuditPage({ client }: AdminAuditPageProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  const rawOutcome = searchParams.get('outcome') ?? '';
  const outcome = outcomes.has(rawOutcome as AuditOutcome)
    ? (rawOutcome as AuditOutcome)
    : '';
  const action = (searchParams.get('action') ?? '').trim();

  const load = useCallback(
    () =>
      client.listAuditEvents({
        limit: 50,
        ...(outcome ? { outcome } : {}),
        ...(action ? { action } : {}),
      }),
    [action, client, outcome],
  );
  const { state, reload } = useAdminResource<AuditEventPage>(load);
  const [extra, setExtra] = useState<readonly AuditEvent[]>([]);
  const [extraCursor, setExtraCursor] = useState<string>();
  const [extending, setExtending] = useState(false);
  const [failure, setFailure] = useState('');
  const [pageKey, setPageKey] = useState('');

  const key = `${action}|${outcome}`;
  if (pageKey !== key) {
    setPageKey(key);
    setExtra([]);
    setExtraCursor(undefined);
    setFailure('');
  }

  const items =
    state.kind === 'ready' ? [...state.value.items, ...extra] : extra;
  const cursor =
    extraCursor ?? (state.kind === 'ready' ? state.value.nextCursor : undefined);

  async function showEarlier() {
    if (!cursor) {
      return;
    }

    setExtending(true);
    setFailure('');
    try {
      const response = await client.listAuditEvents({
        limit: 50,
        cursor,
        ...(outcome ? { outcome } : {}),
        ...(action ? { action } : {}),
      });
      setExtra((current) => [...current, ...response.data.items]);
      setExtraCursor(response.data.nextCursor);
    } catch {
      setFailure('Earlier events could not be loaded. Try again in a moment.');
    } finally {
      setExtending(false);
    }
  }

  function updateParam(name: string, value: string) {
    const next = new URLSearchParams(searchParams);
    if (value) {
      next.set(name, value);
    } else {
      next.delete(name);
    }
    setSearchParams(next);
  }

  return (
    <AdminPage
      title="Audit log"
      description="Every privileged action with its actor, outcome, and time. Entries are read-only and cannot be edited from the gallery."
      toolbar={
        <>
          <div className={styles.filterField}>
            <label htmlFor="audit-outcome">Outcome</label>
            <select
              className={styles.control}
              id="audit-outcome"
              value={outcome}
              onChange={(event) => updateParam('outcome', event.target.value)}
            >
              <option value="">Any outcome</option>
              {(['succeeded', 'denied', 'failed'] as const).map((value) => (
                <option key={value} value={value}>
                  {outcomeLabels[value]}
                </option>
              ))}
            </select>
          </div>
          <div className={styles.filterField}>
            <label htmlFor="audit-action">Action</label>
            <input
              className={styles.control}
              defaultValue={action}
              id="audit-action"
              name="action"
              type="search"
              onBlur={(event) => updateParam('action', event.target.value.trim())}
            />
          </div>
        </>
      }
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Loading audit events…' : ''}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading audit events…" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="Audit events are unavailable"
          description="The audit log could not be read. Recorded events are unaffected."
          onRetry={reload}
        />
      ) : null}

      {failure ? (
        <p className={styles.alert} role="alert">
          {failure}
        </p>
      ) : null}

      {state.kind === 'ready' && items.length === 0 ? (
        <AdminEmpty>No audit events match these filters.</AdminEmpty>
      ) : null}

      {items.length > 0 ? (
        <ol className={styles.timeline} aria-label="Audit events">
          {items.map((event) => (
            <li className={styles.timelineItem} key={event.id}>
              <p className={styles.timelineTime}>
                {formatMoment(event.occurredAt)}
              </p>
              <p className={styles.primaryCell}>{event.action}</p>
              <p className={styles.secondaryCell}>
                {actorLabels[event.actor.kind]}:{' '}
                <span>{event.actor.displayName}</span>
                {event.resourceType
                  ? ` · ${event.resourceType}${
                      event.resourceId ? ` ${event.resourceId}` : ''
                    }`
                  : ''}
              </p>
              <span className={styles.badge} data-status={event.outcome}>
                {outcomeLabels[event.outcome]}
              </span>
            </li>
          ))}
        </ol>
      ) : null}

      {cursor ? (
        <button
          className={styles.secondaryButton}
          disabled={extending}
          type="button"
          onClick={() => void showEarlier()}
        >
          {extending ? 'Loading…' : 'Show earlier events'}
        </button>
      ) : null}
    </AdminPage>
  );
}
