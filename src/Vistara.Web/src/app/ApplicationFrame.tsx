import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Link,
  Outlet,
  useLocation,
  useNavigation,
  useNavigationType,
} from 'react-router-dom';
import { BrandMark } from '../brand';
import { ConnectivityStatus } from './ConnectivityStatus';
import { PrimaryNavigation } from './navigation/PrimaryNavigation';
import { ThemeControl } from './ThemeControl';
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

      <aside className={styles.rail}>
        <Brand />
        <PrimaryNavigation variant="rail" />
        <p className={styles.railFootnote}>Private by default</p>
      </aside>

      <div className={styles.workspace}>
        <header className={styles.header}>
          <div className={styles.mobileBrand}>
            <Brand />
          </div>
          <Link className={styles.search} to="/search">
            <SearchIcon />
            <span>Search your library</span>
          </Link>
          <div className={styles.headerUtilities}>
            <ConnectivityStatus />
            <ThemeControl />
            <Link className={styles.headerUpload} to="/uploads">
              Upload
            </Link>
          </div>
        </header>

        {staticPreview ? (
          <aside
            className={styles.previewBanner}
            aria-label="Static preview notice"
          >
            <strong>Static preview only</strong>—no API, authentication,
            uploads, persistence, or worker processing.
          </aside>
        ) : null}

        {navigation.state !== 'idle' ? (
          <div
            className={styles.loadingBar}
            role="status"
            aria-label="Page loading"
            aria-live="polite"
          >
            Loading page…
          </div>
        ) : null}

        <main className={styles.main} id="main-content" tabIndex={-1}>
          {children ?? <Outlet />}
        </main>

        <footer className={styles.footer}>
          <span>Originals stay yours.</span>
          <span>Vistara archive workspace</span>
        </footer>
      </div>

      <PrimaryNavigation variant="bottom" />
      <RouteTransitionStatus />
    </div>
  );
}

function Brand() {
  return (
    <Link className={styles.brand} to="/library" aria-label="Vistara library">
      <BrandMark className={styles.brandMark} />
      <span className={styles.brandName}>Vistara</span>
    </Link>
  );
}

function SearchIcon() {
  return (
    <svg
      className={styles.searchIcon}
      viewBox="0 0 24 24"
      aria-hidden="true"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeWidth="1.7"
    >
      <circle cx="10.5" cy="10.5" r="6.5" />
      <path d="m16 16 4 4" />
    </svg>
  );
}

function RouteTransitionStatus() {
  const location = useLocation();
  const navigation = useNavigation();
  const navigationType = useNavigationType();
  const handledPathname = useRef(location.pathname);
  const [pageTitle, setPageTitle] = useState('');

  useEffect(() => {
    if (
      navigation.state !== 'idle' ||
      navigationType === 'REPLACE' ||
      handledPathname.current === location.pathname
    ) {
      handledPathname.current = location.pathname;
      return;
    }

    handledPathname.current = location.pathname;
    let observer: MutationObserver | undefined;

    const focusHeading = () => {
      const heading = document.querySelector<HTMLElement>('#main-content h1');
      if (!heading) {
        return false;
      }

      if (!heading.hasAttribute('tabindex')) {
        heading.setAttribute('tabindex', '-1');
      }
      heading.focus({ preventScroll: true });
      setPageTitle(heading.textContent?.trim() ?? '');
      observer?.disconnect();
      return true;
    };

    if (!focusHeading()) {
      const main = document.getElementById('main-content');
      if (main) {
        observer = new MutationObserver(focusHeading);
        observer.observe(main, { childList: true, subtree: true });
      }
    }

    return () => observer?.disconnect();
  }, [location.pathname, navigation.state, navigationType]);

  return (
    <span
      className={styles.visuallyHidden}
      role="status"
      aria-label="Current page"
      aria-live="polite"
      aria-atomic="true"
    >
      {pageTitle}
    </span>
  );
}
