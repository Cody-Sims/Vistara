import {
  isRouteErrorResponse,
  Link,
  useRouteError,
} from 'react-router-dom';
import { ApplicationFrame } from './ApplicationFrame';
import styles from './ShellPages.module.css';

export function InitialLoadingPage() {
  return (
    <p role="status" aria-live="polite">
      Loading Vistara…
    </p>
  );
}

export function LibraryPage() {
  return (
    <section className={styles.panel} aria-labelledby="library-heading">
      <p className={styles.eyebrow}>Your image control plane</p>
      <h1 id="library-heading">Library</h1>
      <p className={styles.description}>
        Your gallery will appear here when library features are enabled.
      </p>
    </section>
  );
}

export function NotFoundPage() {
  return (
    <section className={styles.panel} aria-labelledby="not-found-heading">
      <p className={styles.eyebrow}>404</p>
      <h1 id="not-found-heading">Page not found</h1>
      <p className={styles.description}>
        The requested page does not exist or is not available yet.
      </p>
      <Link className={styles.actionLink} to="/library">
        Return to library
      </Link>
    </section>
  );
}

interface RouteErrorPageProps {
  detail: string;
}

export function RouteErrorPage({ detail }: RouteErrorPageProps) {
  return (
    <section className={styles.panel} aria-labelledby="error-heading">
      <p className={styles.eyebrow}>Application error</p>
      <h1 id="error-heading">Something went wrong</h1>
      <p className={styles.description}>{detail}</p>
      <Link className={styles.actionLink} to="/library">
        Return to library
      </Link>
    </section>
  );
}

export function RouteErrorBoundary() {
  const error = useRouteError();
  const detail = isRouteErrorResponse(error)
    ? `${error.status} ${error.statusText}`
    : 'An unexpected application error occurred.';

  return (
    <ApplicationFrame>
      <RouteErrorPage detail={detail} />
    </ApplicationFrame>
  );
}
