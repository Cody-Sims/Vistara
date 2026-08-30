import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef } from 'react';
import {
  Link,
  Navigate,
  useLocation,
  useNavigate,
  useParams,
  useSearchParams,
} from 'react-router-dom';
import { VistaraApiClient } from '../../api/generated';
import { AlbumDetailView, AlbumsView } from '../../features/albums';
import { FavoritesView } from '../../features/favorites';
import { LibraryPage } from '../../features/library';
import { PublicShareView, ShareManager } from '../../features/shares';
import { TagsView } from '../../features/tags';
import { TrashManager } from '../../features/trash';
import {
  FrozenUploadClient,
  IndexedDbUploadPersistence,
  UploadQueue,
  UploadQueueView,
} from '../../features/uploads';
import { ViewerPage } from '../../features/viewer';
import styles from './galleryRoutes.module.css';

const client = new VistaraApiClient();
const uploadClient = new FrozenUploadClient();
const uploadQueue = new UploadQueue({
  client: uploadClient,
  transfer: uploadClient,
  persistence: new IndexedDbUploadPersistence(),
});

export function LibraryRoute() {
  const location = useLocation();
  useEffect(() => {
    const address = `${location.pathname}${location.search}`;
    const storageKey = `vistara:route-restoration:${address}`;
    const saved = readRouteRestoration(storageKey);
    let frame = 0;
    let cancelled = false;
    let restoring = saved !== null;
    let userAdjusted = saved === null;
    let activeScroller: HTMLElement | null = null;
    const save = () => {
      if (!activeScroller || restoring) {
        return;
      }

      const focusedAssetId =
        activeScroller.querySelector<HTMLElement>('[data-asset-link]:focus')
          ?.dataset.assetLink;
      sessionStorage.setItem(
        storageKey,
        JSON.stringify({
          scrollTop: activeScroller.scrollTop,
          ...(focusedAssetId ? { focusedAssetId } : {}),
        }),
      );
    };
    const markUserAdjusted = () => {
      userAdjusted = true;
    };
    const trackScroll = () => {
      if (
        activeScroller &&
        saved &&
        !userAdjusted &&
        Math.abs(activeScroller.scrollTop - saved.scrollTop) > 1
      ) {
        activeScroller.scrollTop = saved.scrollTop;
        return;
      }

      save();
    };
    const connect = () => {
      if (cancelled) {
        return;
      }

      const scroller = document.querySelector<HTMLElement>(
        '[aria-label="Library timeline"]',
      );
      if (
        !scroller ||
        (saved && scroller.scrollHeight <= scroller.clientHeight)
      ) {
        if (frame++ < 120) {
          requestAnimationFrame(connect);
        }
        return;
      }

      activeScroller = scroller;
      const startTracking = () => {
        restoring = false;
        activeScroller?.addEventListener('scroll', trackScroll);
        activeScroller?.addEventListener('focusin', trackScroll);
        activeScroller?.addEventListener('wheel', markUserAdjusted);
        activeScroller?.addEventListener('touchmove', markUserAdjusted);
        activeScroller?.addEventListener('keydown', markUserAdjusted);
      };
      if (!saved) {
        startTracking();
        return;
      }

      let settleFrames = 0;
      const restore = () => {
        if (cancelled || !activeScroller) {
          return;
        }

        activeScroller.scrollTop = saved.scrollTop;
        if (settleFrames++ < 5) {
          requestAnimationFrame(restore);
        } else {
          startTracking();
        }
      };
      restore();
    };
    requestAnimationFrame(connect);
    return () => {
      cancelled = true;
      activeScroller?.removeEventListener('scroll', trackScroll);
      activeScroller?.removeEventListener('focusin', trackScroll);
      activeScroller?.removeEventListener('wheel', markUserAdjusted);
      activeScroller?.removeEventListener('touchmove', markUserAdjusted);
      activeScroller?.removeEventListener('keydown', markUserAdjusted);
    };
  }, [location.pathname, location.search]);

  return <LibraryPage dataSource={client} />;
}

export function ViewerRoute() {
  return <ViewerPage dataSource={client} />;
}

export function UploadsRoute() {
  useEffect(() => {
    void uploadQueue.restore();
  }, []);
  return <UploadQueueView queue={uploadQueue} />;
}

export function AlbumsRoute() {
  return <AlbumsView client={client} />;
}

export function AlbumRoute() {
  const { albumId } = useParams();
  const navigate = useNavigate();
  if (!albumId) {
    return <Navigate replace to="/albums" />;
  }

  return (
    <AlbumDetailView
      albumId={albumId}
      client={client}
      onDeleted={() => void navigate('/albums')}
    />
  );
}

export function TagsRoute() {
  const { tagId } = useParams();
  const navigate = useNavigate();
  return (
    <TagsView
      client={client}
      selectedTagIds={tagId ? [tagId] : []}
      onFilterChange={(tagIds) =>
        void navigate(tagIds[0] ? `/tags/${encodeURIComponent(tagIds[0])}` : '/tags')
      }
    />
  );
}

export function FavoritesRoute() {
  return <FavoritesView client={client} />;
}

export function SharesRoute() {
  const [searchParams] = useSearchParams();
  const albumId = searchParams.get('albumId');
  const assetId = searchParams.get('assetId');
  const version = Number(searchParams.get('version'));
  const needsDefaultAsset =
    albumId === null && !(assetId && Number.isSafeInteger(version) && version > 0);
  const firstAsset = useQuery({
    queryKey: ['share-route-default-asset'],
    queryFn: () => client.listAssets({ limit: 1 }),
    enabled: needsDefaultAsset,
  });
  const target = useMemo(() => {
    if (albumId) {
      return { kind: 'album' as const, albumId };
    }

    if (assetId && Number.isSafeInteger(version) && version > 0) {
      return {
        kind: 'snapshot' as const,
        assets: [{ id: assetId, version }],
      };
    }

    const asset = firstAsset.data?.data.items[0];
    return asset
      ? {
          kind: 'snapshot' as const,
          assets: [{ id: asset.id, version: asset.version }],
        }
      : undefined;
  }, [albumId, assetId, firstAsset.data, version]);

  if (firstAsset.isPending && needsDefaultAsset) {
    return <p role="status">Loading share targets…</p>;
  }

  if (!target) {
    return (
      <section className={styles.preview} aria-labelledby="shares-heading">
        <p className={styles.eyebrow}>Sharing</p>
        <h1 id="shares-heading">Share links</h1>
        <p className={styles.previewMessage}>
          Upload an image or choose an album before creating a share link.
        </p>
      </section>
    );
  }

  return <ShareManager client={client} target={target} />;
}

export function TrashRoute() {
  return (
    <TrashManager
      client={client}
      reauthenticate={async () => false}
    />
  );
}

export function PublicShareRoute() {
  const { token } = useParams();
  return token ? (
    <PublicShareView client={client} token={token} />
  ) : (
    <AccessibleNotFoundRoute />
  );
}

export function RoutePlaceholderPage({
  title,
  staticPreview,
}: {
  readonly title: string;
  readonly staticPreview: boolean;
}) {
  return (
    <section className={styles.preview} aria-labelledby="preview-heading">
      <p className={styles.eyebrow}>Gallery preview</p>
      <h1 id="preview-heading">{title}</h1>
      <p className={styles.previewMessage} role={staticPreview ? 'note' : undefined}>
        {staticPreview
          ? 'Static preview only. This page does not connect to an API.'
          : 'The gallery route is ready.'}
      </p>
    </section>
  );
}

export function AccessibleNotFoundRoute() {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);

  return (
    <section className={styles.notFound} aria-labelledby="not-found-heading">
      <p className={styles.eyebrow}>404</p>
      <h1 id="not-found-heading" ref={heading} tabIndex={-1}>
        Page not found
      </h1>
      <p className={styles.previewMessage}>
        The requested page does not exist.
      </p>
      <Link className={styles.returnLink} to="/library">
        Return to library
      </Link>
    </section>
  );
}

function readRouteRestoration(storageKey: string) {
  try {
    const value = JSON.parse(sessionStorage.getItem(storageKey) ?? '') as {
      scrollTop?: unknown;
      focusedAssetId?: unknown;
    };
    return typeof value.scrollTop === 'number' &&
      Number.isFinite(value.scrollTop) &&
      value.scrollTop >= 0 &&
      (value.focusedAssetId === undefined ||
        typeof value.focusedAssetId === 'string')
      ? {
          scrollTop: value.scrollTop,
          focusedAssetId: value.focusedAssetId,
        }
      : null;
  } catch {
    return null;
  }
}
