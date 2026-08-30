import {
  useCallback,
  useEffect,
  useState,
  type FormEvent,
} from 'react';
import {
  VistaraApiError,
  type VistaraApiClient,
} from '../../api/generated/client';
import type {
  AlbumDetail,
  AlbumItem,
  AlbumSummary,
  EntityTag,
} from '../../api/generated/models';
import styles from './albums.module.css';
import { Skeleton } from '../../components';

type AlbumsClient = Pick<VistaraApiClient, 'listAlbums' | 'createAlbum'>;

type AlbumDetailClient = Pick<
  VistaraApiClient,
  | 'getAlbum'
  | 'updateAlbum'
  | 'deleteAlbum'
  | 'reorderAlbumItems'
  | 'removeAlbumItems'
>;

interface AlbumsViewProps {
  client: AlbumsClient;
}

interface AlbumDetailViewProps {
  albumId: string;
  client: AlbumDetailClient;
  onDeleted?: () => void;
}

type ItemResult = {
  id: string;
  title: string;
  outcome: 'Removed' | 'Conflict — restored';
};

export function AlbumsView({ client }: AlbumsViewProps) {
  const [albums, setAlbums] = useState<readonly AlbumSummary[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [message, setMessage] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setState('loading');
    setMessage('');
    try {
      const response = await client.listAlbums();
      setAlbums(response.data.items);
      setState('ready');
    } catch {
      setState('error');
    }
  }, [client]);

  useEffect(() => {
    let active = true;
    void client.listAlbums().then(
      (response) => {
        if (!active) return;
        setAlbums(response.data.items);
        setState('ready');
      },
      () => {
        if (active) setState('error');
      },
    );
    return () => {
      active = false;
    };
  }, [client]);

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const trimmedName = name.trim();
    if (!trimmedName) {
      setMessage('Enter an album name.');
      return;
    }

    setCreating(true);
    setMessage('');
    try {
      const response = await client.createAlbum(
        {
          name: trimmedName,
          ...(description.trim() ? { description: description.trim() } : {}),
        },
        { idempotencyKey: createIdempotencyKey() },
      );
      setAlbums((current) => [response.data.album, ...current]);
      setName('');
      setDescription('');
      setMessage(`${response.data.album.name} was created.`);
    } catch {
      setMessage('The album could not be created. Try again.');
    } finally {
      setCreating(false);
    }
  }

  return (
    <section className={styles.page} aria-labelledby="albums-heading">
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Gallery curation</p>
          <h1 id="albums-heading">Albums</h1>
        </div>
        <p>Group images into ordered, easy-to-browse collections.</p>
      </header>

      <form className={styles.panel} onSubmit={create}>
        <h2>Create album</h2>
        <div className={styles.formGrid}>
          <label>
            Album name
            <input
              value={name}
              onChange={(event) => setName(event.target.value)}
              maxLength={255}
              required
            />
          </label>
          <label>
            Description <span className={styles.optional}>(optional)</span>
            <textarea
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              rows={2}
            />
          </label>
        </div>
        <button type="submit" disabled={creating}>
          {creating ? 'Creating…' : 'Create album'}
        </button>
      </form>

      {message ? (
        <p className={styles.notice} role="status">
          {message}
        </p>
      ) : null}

      {state === 'loading' ? (
        <div aria-busy="true">
          <p role="status">Loading albums…</p>
          <Skeleton count={4} shape="card" />
        </div>
      ) : null}
      {state === 'error' ? (
        <div className={styles.error} role="alert">
          <p>Albums could not be loaded.</p>
          <button type="button" onClick={() => void load()}>
            Try again
          </button>
        </div>
      ) : null}
      {state === 'ready' && albums.length === 0 ? (
        <p className={styles.empty}>
          No albums yet. Create one to organize your gallery.
        </p>
      ) : null}
      {state === 'ready' && albums.length > 0 ? (
        <ul className={styles.albumGrid}>
          {albums.map((item) => (
            <li key={item.id}>
              <a className={styles.albumCard} href={`/albums/${item.id}`}>
                {item.cover ? (
                  <img
                    src={item.cover.path}
                    alt=""
                    width={item.cover.width}
                    height={item.cover.height}
                  />
                ) : (
                  <span className={styles.coverPlaceholder} aria-hidden="true">
                    ◫
                  </span>
                )}
                <span>
                  <strong>{item.name}</strong>
                  <small>
                    {item.itemCount} {item.itemCount === 1 ? 'image' : 'images'}
                  </small>
                </span>
              </a>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}

export function AlbumDetailView({
  albumId,
  client,
  onDeleted,
}: AlbumDetailViewProps) {
  const [detail, setDetail] = useState<AlbumDetail>();
  const [etag, setEtag] = useState<EntityTag>();
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [name, setName] = useState('');
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState('');
  const [alert, setAlert] = useState('');
  const [results, setResults] = useState<readonly ItemResult[]>([]);

  const applyDetail = useCallback(
    (next: AlbumDetail, responseEtag?: EntityTag) => {
      setDetail(next);
      setName(next.album.name);
      setEtag(responseEtag ?? versionTag(next.album.version));
    },
    [],
  );

  const load = useCallback(async () => {
    setState('loading');
    setAlert('');
    try {
      const response = await client.getAlbum(albumId);
      applyDetail(response.data, response.etag);
      setState('ready');
    } catch {
      setState('error');
    }
  }, [albumId, applyDetail, client]);

  useEffect(() => {
    let active = true;
    void client.getAlbum(albumId).then(
      (response) => {
        if (!active) return;
        applyDetail(response.data, response.etag);
        setState('ready');
      },
      () => {
        if (active) setState('error');
      },
    );
    return () => {
      active = false;
    };
  }, [albumId, applyDetail, client]);

  async function reconcileConflict(messageText: string) {
    try {
      const response = await client.getAlbum(albumId);
      applyDetail(response.data, response.etag);
      setSelected(new Set());
      setAlert(messageText);
    } catch {
      setAlert('The latest album could not be loaded. Try again.');
    }
  }

  async function rename(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!detail || !etag || !name.trim() || name.trim() === detail.album.name) {
      return;
    }

    const previous = detail;
    const nextName = name.trim();
    setDetail({
      ...detail,
      album: { ...detail.album, name: nextName },
    });
    setPending(true);
    setAlert('');
    try {
      const response = await client.updateAlbum(
        albumId,
        { name: nextName },
        { ifMatch: etag, idempotencyKey: createIdempotencyKey() },
      );
      applyDetail(response.data, response.etag);
      setMessage('Album name saved.');
    } catch (error) {
      if (isConflict(error)) {
        await reconcileConflict(
          'This album changed elsewhere. The latest version was restored.',
        );
      } else {
        applyDetail(previous, etag);
        setAlert('The album name could not be saved.');
      }
    } finally {
      setPending(false);
    }
  }

  async function move(index: number, direction: -1 | 1) {
    if (!detail || !etag) {
      return;
    }
    const target = index + direction;
    if (target < 0 || target >= detail.items.items.length) {
      return;
    }

    const previous = detail;
    const reordered = [...detail.items.items];
    [reordered[index], reordered[target]] = [
      reordered[target]!,
      reordered[index]!,
    ];
    const positioned = reordered.map((item, itemIndex) => ({
      ...item,
      position: (itemIndex + 1) * 100,
    }));
    setDetail({ ...detail, items: { ...detail.items, items: positioned } });
    setPending(true);
    setAlert('');
    try {
      const response = await client.reorderAlbumItems(
        albumId,
        {
          items: positioned.map((item) => ({
            assetId: item.asset.id,
            position: item.position,
          })),
        },
        { ifMatch: etag, idempotencyKey: createIdempotencyKey() },
      );
      applyDetail(response.data, response.etag);
      setMessage('Album order saved.');
    } catch (error) {
      if (isConflict(error)) {
        await reconcileConflict(
          'This album changed elsewhere. The latest order was restored.',
        );
      } else {
        applyDetail(previous, etag);
        setAlert('The new order could not be saved.');
      }
    } finally {
      setPending(false);
    }
  }

  async function removeSelected() {
    if (!detail || !etag || selected.size === 0) {
      return;
    }
    const removed = detail.items.items.filter((item) =>
      selected.has(item.asset.id),
    );
    const previous = detail;
    setDetail({
      ...detail,
      album: {
        ...detail.album,
        itemCount: detail.album.itemCount - removed.length,
      },
      items: {
        ...detail.items,
        items: detail.items.items.filter(
          (item) => !selected.has(item.asset.id),
        ),
      },
    });
    setSelected(new Set());
    setPending(true);
    setResults([]);
    setAlert('');
    try {
      const response = await client.removeAlbumItems(
        albumId,
        {
          items: removed.map((item) => ({
            id: item.asset.id,
            version: item.asset.version,
          })),
        },
        { ifMatch: etag, idempotencyKey: createIdempotencyKey() },
      );
      applyDetail(response.data, response.etag);
      setResults(
        removed.map((item) => ({
          id: item.asset.id,
          title: item.asset.title,
          outcome: 'Removed',
        })),
      );
    } catch (error) {
      if (isConflict(error)) {
        await reconcileConflict(
          'This album changed elsewhere. Conflicting removals were restored.',
        );
        setResults(
          removed.map((item) => ({
            id: item.asset.id,
            title: item.asset.title,
            outcome: 'Conflict — restored',
          })),
        );
      } else {
        applyDetail(previous, etag);
        setAlert('Selected images could not be removed.');
      }
    } finally {
      setPending(false);
    }
  }

  async function deleteAlbum() {
    if (!detail || !etag) {
      return;
    }
    setPending(true);
    setAlert('');
    try {
      await client.deleteAlbum(albumId, {
        ifMatch: etag,
        idempotencyKey: createIdempotencyKey(),
      });
      setMessage(`${detail.album.name} was deleted.`);
      onDeleted?.();
    } catch (error) {
      if (isConflict(error)) {
        await reconcileConflict(
          'This album changed elsewhere. Review the latest version before deleting.',
        );
      } else {
        setAlert('The album could not be deleted.');
      }
    } finally {
      setPending(false);
    }
  }

  if (state === 'loading') {
    return <p role="status">Loading album…</p>;
  }
  if (state === 'error' || !detail) {
    return (
      <div className={styles.error} role="alert">
        <p>The album could not be loaded.</p>
        <button type="button" onClick={() => void load()}>
          Try again
        </button>
      </div>
    );
  }

  return (
    <section className={styles.page} aria-labelledby="album-heading">
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Album</p>
          <h1 id="album-heading">{detail.album.name}</h1>
        </div>
        <p>{detail.album.itemCount} images</p>
      </header>

      {alert ? (
        <p className={styles.error} role="alert">
          {alert}
        </p>
      ) : null}
      {message ? (
        <p className={styles.notice} role="status">
          {message}
        </p>
      ) : null}

      <form className={styles.rename} onSubmit={rename}>
        <label>
          Album name
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            maxLength={255}
            required
          />
        </label>
        <button type="submit" disabled={pending}>
          Save album name
        </button>
      </form>

      <div className={styles.toolbar} aria-label="Album actions">
        <button
          type="button"
          onClick={() => void removeSelected()}
          disabled={pending || selected.size === 0}
        >
          Remove selected
        </button>
        <span aria-live="polite">{selected.size} selected</span>
      </div>

      {detail.items.items.length === 0 ? (
        <p className={styles.empty}>This album is empty.</p>
      ) : (
        <ol className={styles.itemList}>
          {detail.items.items.map((item, index) => (
            <AlbumItemRow
              key={item.asset.id}
              item={item}
              index={index}
              count={detail.items.items.length}
              selected={selected.has(item.asset.id)}
              disabled={pending}
              onSelect={(checked) => {
                setSelected((current) => {
                  const next = new Set(current);
                  if (checked) next.add(item.asset.id);
                  else next.delete(item.asset.id);
                  return next;
                });
              }}
              onMove={(direction) => void move(index, direction)}
            />
          ))}
        </ol>
      )}

      {results.length > 0 ? (
        <section aria-labelledby="album-results">
          <h2 id="album-results">Removal results</h2>
          <ul className={styles.results} aria-live="polite">
            {results.map((result) => (
              <li key={result.id}>
                {result.title}: {result.outcome}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section className={styles.danger} aria-labelledby="delete-album">
        <h2 id="delete-album">Delete album</h2>
        <p>Images stay in the library when an album is deleted.</p>
        <button type="button" disabled={pending} onClick={() => void deleteAlbum()}>
          Delete album
        </button>
      </section>
    </section>
  );
}

interface AlbumItemRowProps {
  item: AlbumItem;
  index: number;
  count: number;
  selected: boolean;
  disabled: boolean;
  onSelect: (checked: boolean) => void;
  onMove: (direction: -1 | 1) => void;
}

function AlbumItemRow({
  item,
  index,
  count,
  selected,
  disabled,
  onSelect,
  onMove,
}: AlbumItemRowProps) {
  return (
    <li className={styles.item}>
      <label className={styles.selectItem}>
        <input
          type="checkbox"
          checked={selected}
          onChange={(event) => onSelect(event.target.checked)}
          aria-label={`Select ${item.asset.title}`}
        />
        <span>{item.asset.title}</span>
      </label>
      <div className={styles.orderButtons}>
        <button
          type="button"
          disabled={disabled || index === 0}
          onClick={() => onMove(-1)}
          aria-label={`Move ${item.asset.title} up`}
        >
          ↑
        </button>
        <button
          type="button"
          disabled={disabled || index === count - 1}
          onClick={() => onMove(1)}
          aria-label={`Move ${item.asset.title} down`}
        >
          ↓
        </button>
      </div>
    </li>
  );
}

function versionTag(version: number): EntityTag {
  return `"v${version}"`;
}

function isConflict(error: unknown): boolean {
  return error instanceof VistaraApiError && error.status === 412;
}

function createIdempotencyKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `web-${Date.now()}`;
}
