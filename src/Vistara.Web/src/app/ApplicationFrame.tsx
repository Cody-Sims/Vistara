import type { ReactNode } from 'react';
import { Link, Outlet, useNavigation } from 'react-router-dom';
import { PrimaryNavigation } from './navigation/PrimaryNavigation';
import styles from './ApplicationFrame.module.css';

interface ApplicationFrameProps {
  children?: ReactNode;
  staticPreview?: boolean;
}

export function ApplicationFrame({
  children,
  staticPreview = false,
}: ApplicationFrameProps) {
  const navigation = useNavigation();

  return (
    <div className={styles.application}>
      <a className={styles.skipLink} href="#main-content">
        Skip to content
      </a>

      <header className={styles.header}>
        <Link className={styles.brand} to="/library" aria-label="Vistara library">
          <span aria-hidden="true" className={styles.brandMark}>
            V
          </span>
          <span>Vistara</span>
        </Link>

        <PrimaryNavigation />
      </header>

      {staticPreview ? (
        <aside
          className={styles.previewBanner}
          aria-label="Static preview notice"
        >
          <strong>Static preview only</strong>—no API, authentication, uploads,
          persistence, or worker processing.
        </aside>
      ) : null}

      {navigation.state !== 'idle' ? (
        <div className={styles.loadingBar} role="status" aria-live="polite">
          Loading page…
        </div>
      ) : null}

      <main className={styles.main} id="main-content" tabIndex={-1}>
        {children ?? <Outlet />}
      </main>

      <footer className={styles.footer}>
        <span>Private by default.</span>
        <span>Built for your library.</span>
      </footer>
    </div>
  );
}
