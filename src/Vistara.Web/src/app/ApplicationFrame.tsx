import type { ReactNode } from 'react';
import { Link, NavLink, Outlet, useNavigation } from 'react-router-dom';
import styles from './ApplicationFrame.module.css';

interface ApplicationFrameProps {
  children?: ReactNode;
}

export function ApplicationFrame({ children }: ApplicationFrameProps) {
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

        <nav aria-label="Primary navigation">
          <NavLink
            className={({ isActive }) =>
              isActive ? `${styles.navLink} ${styles.activeNavLink}` : styles.navLink
            }
            to="/library"
          >
            Library
          </NavLink>
        </nav>
      </header>

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
