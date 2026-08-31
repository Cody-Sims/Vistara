import type { ReactNode } from 'react';
import { Link, Navigate, useLocation } from 'react-router-dom';
import { Skeleton, StatusMessage } from '../../components';
import type { SessionScope } from './roles';
import { useSession } from './sessionContext';
import styles from './session.module.css';

interface GuardProps {
  readonly children: ReactNode;
}

export interface AdministrationGuardProps extends GuardProps {
  /** The scope the screen behind this guard spends on its first request. */
  readonly scope?: SessionScope;
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

export function RequireAdministration({
  children,
  scope,
}: AdministrationGuardProps) {
  return (
    <RequireSession>
      <AdministrationGate scope={scope}>{children}</AdministrationGate>
    </RequireSession>
  );
}

/**
 * Administration is opened by what the credential may spend, not by the role
 * it reports. A tenant-bound credential reaches this workspace read-only, so
 * it is told that rather than being shown screens the API will refuse.
 */
function AdministrationGate({ children, scope }: AdministrationGuardProps) {
  const session = useSession();
  const authorized =
    session.canAdminister &&
    (scope === undefined || session.scopes.includes(scope));

  if (authorized) {
    return <>{children}</>;
  }

  if (session.credentialKind === 'tenantBound') {
    return (
      <section
        className={styles.guard}
        aria-labelledby="admin-credential-heading"
      >
        <p className={styles.eyebrow}>Administration</p>
        <h1 id="admin-credential-heading">
          Administration needs a signed-in session
        </h1>
        <p className={styles.description}>
          This workspace was reached with a workspace credential, such as an
          API key. Those credentials read and write gallery content only, so
          administration is unavailable however the workspace names your role.
          Sign in with your account to administer it.
        </p>
        <div className={styles.actions}>
          <Link className={styles.primaryButton} to="/library">
            Return to library
          </Link>
          <Link className={styles.secondaryLink} to="/login">
            Sign in
          </Link>
        </div>
      </section>
    );
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
