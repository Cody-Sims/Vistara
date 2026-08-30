import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef } from 'react';
import {
  Link,
  useLocation,
  useNavigate,
  useParams,
} from 'react-router-dom';
import type { ApiResponse, AssetDetail } from '../../api/generated';
import { buildResponsiveImage } from './responsiveImage';
import {
  captureFocusRestorer,
  getViewerReturnAddress,
} from './viewerState';
import styles from './ViewerPage.module.css';

export interface ViewerDataSource {
  getAsset(assetId: string): Promise<ApiResponse<AssetDetail>>;
}

interface ViewerPageProps {
  dataSource: ViewerDataSource;
  neighborIds?: {
    previous?: string;
    next?: string;
  };
}

function formatBytes(bytes: number) {
  return new Intl.NumberFormat(undefined, {
    style: 'unit',
    unit: bytes >= 1_000_000 ? 'megabyte' : 'kilobyte',
    unitDisplay: 'short',
    maximumFractionDigits: 1,
  }).format(bytes / (bytes >= 1_000_000 ? 1_000_000 : 1_000));
}

export function ViewerPage({ dataSource, neighborIds }: ViewerPageProps) {
  const { assetId } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const returnAddress = useMemo(
    () => getViewerReturnAddress(location.state),
    [location.state],
  );
  const query = useQuery({
    queryKey: ['asset-viewer', assetId],
    queryFn: () => {
      if (!assetId) throw new Error('Missing asset id');
      return dataSource.getAsset(assetId);
    },
    enabled: Boolean(assetId),
  });
  const detail = query.data?.data;
  const responsive = detail
    ? buildResponsiveImage(detail.asset, 'viewer', true)
    : null;

  useEffect(() => captureFocusRestorer(), []);

  useEffect(() => {
    if (detail) headingRef.current?.focus({ preventScroll: true });
  }, [detail]);

  useEffect(() => {
    function handleKeyDown(event: globalThis.KeyboardEvent) {
      if (event.key === 'Escape') {
        event.preventDefault();
        void navigate(returnAddress);
        return;
      }

      if (
        event.target instanceof HTMLInputElement ||
        event.target instanceof HTMLTextAreaElement ||
        event.target instanceof HTMLSelectElement
      ) {
        return;
      }

      const destination =
        event.key === 'ArrowLeft'
          ? neighborIds?.previous
          : event.key === 'ArrowRight'
            ? neighborIds?.next
            : undefined;
      if (!destination) return;
      event.preventDefault();
      void navigate(`/assets/${encodeURIComponent(destination)}`, {
        state: location.state,
      });
    }

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [location.state, navigate, neighborIds, returnAddress]);

  if (!assetId) {
    return (
      <section className={styles.statePanel} role="alert">
        <h1>Asset unavailable</h1>
        <p>The asset address is incomplete.</p>
        <Link to={returnAddress}>Return to library</Link>
      </section>
    );
  }

  if (query.isPending) {
    return (
      <p className={styles.statePanel} role="status" aria-live="polite">
        Loading asset…
      </p>
    );
  }

  if (query.isError || !detail) {
    return (
      <section className={styles.statePanel} role="alert">
        <h1>Could not load this asset</h1>
        <p>Check your connection and try again.</p>
        <button onClick={() => void query.refetch()} type="button">
          Retry
        </button>
        <Link to={returnAddress}>Return to library</Link>
      </section>
    );
  }

  const { asset, metadata } = detail;
  const capturedAt = metadata.capturedAt ?? asset.capturedAt;

  return (
    <article className={styles.viewer} aria-labelledby="asset-title">
      <header className={styles.toolbar}>
        <Link className={styles.close} to={returnAddress}>
          Back to library
        </Link>
        <nav aria-label="Asset navigation" className={styles.navigation}>
          {neighborIds?.previous ? (
            <Link
              aria-label="Previous asset"
              state={location.state}
              to={`/assets/${encodeURIComponent(neighborIds.previous)}`}
            >
              Previous
            </Link>
          ) : null}
          {neighborIds?.next ? (
            <Link
              aria-label="Next asset"
              state={location.state}
              to={`/assets/${encodeURIComponent(neighborIds.next)}`}
            >
              Next
            </Link>
          ) : null}
        </nav>
      </header>

      <div className={styles.content}>
        <figure className={styles.stage}>
          {responsive ? (
            <img {...responsive} />
          ) : (
            <div className={styles.processing} role="status" aria-live="polite">
              <strong>
                {asset.status === 'processing'
                  ? 'Preview is still processing'
                  : asset.status === 'failed'
                    ? 'Preview processing failed'
                    : 'Preview unavailable'}
              </strong>
              <span>
                {asset.status === 'processing'
                  ? 'This page will keep the stable asset address while a derivative is prepared.'
                  : 'The original asset remains protected. Try again or return to the library.'}
              </span>
              <button onClick={() => void query.refetch()} type="button">
                Check again
              </button>
            </div>
          )}
          <figcaption>{asset.description || asset.title}</figcaption>
        </figure>

        <aside className={styles.details} aria-labelledby="asset-title">
          <p className={styles.eyebrow}>
            {asset.favorite ? 'Favorite · ' : ''}
            {asset.visibility}
          </p>
          <h1 id="asset-title" ref={headingRef} tabIndex={-1}>
            {asset.title}
          </h1>
          <dl>
            <div>
              <dt>Dimensions</dt>
              <dd>
                {asset.width.toLocaleString()} × {asset.height.toLocaleString()}
              </dd>
            </div>
            <div>
              <dt>Format</dt>
              <dd>{asset.format.toUpperCase()}</dd>
            </div>
            <div>
              <dt>Size</dt>
              <dd>{formatBytes(asset.sizeBytes)}</dd>
            </div>
            <div>
              <dt>{capturedAt ? 'Captured' : 'Imported'}</dt>
              <dd>
                {new Intl.DateTimeFormat(undefined, {
                  dateStyle: 'long',
                  timeStyle: 'short',
                }).format(new Date(capturedAt ?? asset.importedAt))}
              </dd>
            </div>
            {metadata.cameraMake || metadata.cameraModel ? (
              <div>
                <dt>Camera</dt>
                <dd>
                  {[metadata.cameraMake, metadata.cameraModel]
                    .filter(Boolean)
                    .join(' ')}
                </dd>
              </div>
            ) : null}
          </dl>
          {asset.tags.length > 0 ? (
            <div>
              <h2>Tags</h2>
              <ul className={styles.tags}>
                {asset.tags.map((tag) => (
                  <li key={tag.id}>{tag.name}</li>
                ))}
              </ul>
            </div>
          ) : null}
        </aside>
      </div>
    </article>
  );
}
