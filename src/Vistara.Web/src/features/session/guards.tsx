import type { ReactNode } from 'react';
import { Link, Navigate, useLocation } from 'react-router-dom';
import { Skeleton, StatusMessage } from '../../components';
import { useSession } from './sessionContext';
import styles from './session.module.css';

interface GuardProps {
  readonly children: ReactNode;
}

export function RequireSession({ children }: GuardProps) {
  const session = useSession();
  const location = useLocation();

  if (session.status === 'authenticated' || session.status === 'preview') {
    return <>{children}</>;
  }

  if (session.status === 'loading') {
    return (
      <section className={styles.guard} aria-busy="true">
        <StatusMessage
          live
          tone="pending"
          title="Checking your session…"
          description="Vistara is confirming that you are still signed in."
        />
        <Skeleton count={4} shape="row" />
      </section>
    );
  }

  if (session.status === 'error') {
    return (
      <section className={styles.guard} aria-labelledby="session-error-heading">
        <p className={styles.eyebrow}>Session</p>
        <h1 id="session-error-heading">Session unavailable</h1>
        <p className={styles.description}>
          Vistara could not confirm your session. Your work is untouched; check
          your connection and try again.
        </p>
        <div className={styles.actions}>
          <button
            className={styles.primaryButton}
            type="button"
            onClick={() => void session.reload()}
          >
            Try again
          </button>
          <Link className={styles.secondaryLink} to="/login">
            Sign in again
          </Link>
        </div>
      </section>
    );
  }

  const returnTo = `${location.pathname}${location.search}`;
  return (
    <Navigate
      replace
      to={`/login?returnTo=${encodeURIComponent(returnTo)}`}
    />
  );
}

export function RequireAdministration({ children }: GuardProps) {
  return (
    <RequireSession>
      <AdministrationGate>{children}</AdministrationGate>
    </RequireSession>
  );
}

function AdministrationGate({ children }: GuardProps) {
  const session = useSession();

  if (session.canAdminister) {
    return <>{children}</>;
  }

  return (
    <section className={styles.guard} aria-labelledby="admin-denied-heading">
      <p className={styles.eyebrow}>Administration</p>
      <h1 id="admin-denied-heading">Administration unavailable</h1>
      <p className={styles.description}>
        Your account does not administer this workspace. Ask an owner or
        administrator if you need access.
      </p>
      <div className={styles.actions}>
        <Link className={styles.primaryButton} to="/library">
          Return to library
        </Link>
      </div>
    </section>
  );
}
