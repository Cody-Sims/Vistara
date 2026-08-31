import {
  useCallback,
  useEffect,
  useState,
} from 'react';
import {
  VistaraApiError,
  type VistaraApiClient,
} from '../../api/generated/client';
import type {
  AssetSummary,
} from '../../api/generated/models';
import styles from './favorites.module.css';
import { versionTag } from '../../api/versionTag';

type FavoritesClient = Pick<
  VistaraApiClient,
  'listAssets' | 'getAsset' | 'favoriteAsset' | 'unfavoriteAsset'
>;

interface FavoritesViewProps {
  client: FavoritesClient;
}

interface FavoriteButtonProps {
  asset: AssetSummary;
  client: Pick<
    VistaraApiClient,
    'getAsset' | 'favoriteAsset' | 'unfavoriteAsset'
  >;
  onChange: (asset: AssetSummary) => void;
  onConflict?: (message: string) => void;
}

type BulkResult = {
  id: string;
  title: string;
  outcome: 'Removed' | 'Conflict — kept favorite' | 'Failed — try again';
};

export function FavoriteButton({
  asset,
  client,
  onChange,
  onConflict,
}: FavoriteButtonProps) {
  const [pending, setPending] = useState(false);

  async function toggle() {
    const optimistic = { ...asset, favorite: !asset.favorite };
    onChange(optimistic);
    setPending(true);
    try {
      const mutate = asset.favorite
        ? client.unfavoriteAsset.bind(client)
        : client.favoriteAsset.bind(client);
      const response = await mutate(asset.id, {
        ifMatch: versionTag(asset.version),
        idempotencyKey: createIdempotencyKey(),
      });
      onChange(response.data.asset);
    } catch (error) {
      if (isConflict(error)) {
        try {
          const response = await client.getAsset(asset.id);
          onChange(response.data.asset);
          onConflict?.(
            `${asset.title} changed elsewhere. The latest version was restored.`,
          );
        } catch {
          onChange(asset);
          onConflict?.(`${asset.title} could not be refreshed.`);
        }
      } else {
        onChange(asset);
        onConflict?.(`${asset.title} could not be updated.`);
      }
    } finally {
      setPending(false);
    }
  }

  const action = asset.favorite ? 'Remove' : 'Add';
  return (
    <button
      type="button"
      className={asset.favorite ? styles.favorite : styles.secondary}
      disabled={pending}
      onClick={() => void toggle()}
      aria-pressed={asset.favorite}
      aria-label={`${action} ${asset.title} ${asset.favorite ? 'from' : 'to'} favorites`}
    >
      <span aria-hidden="true">{asset.favorite ? '★' : '☆'}</span>
      {pending ? ' Saving…' : asset.favorite ? ' Favorited' : ' Favorite'}
    </button>
  );
}

export function FavoritesView({ client }: FavoritesViewProps) {
  const [assets, setAssets] = useState<readonly AssetSummary[]>([]);
  const [nextCursor, setNextCursor] = useState<string>();
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [selectingAll, setSelectingAll] = useState(false);
  const [bulkPending, setBulkPending] = useState(false);
  const [results, setResults] = useState<readonly BulkResult[]>([]);
  const [alert, setAlert] = useState('');

  const load = useCallback(async () => {
    setState('loading');
    setAlert('');
    try {
      const response = await client.listAssets({ favorite: true });
      setAssets(response.data.items);
      setNextCursor(response.data.nextCursor);
      setSelected(new Set());
      setState('ready');
    } catch {
      setState('error');
    }
  }, [client]);

  useEffect(() => {
    let active = true;
    void client.listAssets({ favorite: true }).then(
      (response) => {
        if (!active) return;
        setAssets(response.data.items);
        setNextCursor(response.data.nextCursor);
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

  function updateAsset(next: AssetSummary) {
    setAssets((current) => {
      const exists = current.some((asset) => asset.id === next.id);
      if (!next.favorite) {
        return current.filter((asset) => asset.id !== next.id);
      }
      return exists
        ? current.map((asset) => (asset.id === next.id ? next : asset))
        : [...current, next];
    });
  }

  async function selectAllResults() {
    setSelectingAll(true);
    setAlert('');
    try {
      let cursor = nextCursor;
      let collected = [...assets];
      const seenCursors = new Set<string>();
      while (cursor && !seenCursors.has(cursor)) {
        seenCursors.add(cursor);
        const response = await client.listAssets({ favorite: true, cursor });
        const known = new Set(collected.map((asset) => asset.id));
        collected = [
          ...collected,
          ...response.data.items.filter((asset) => !known.has(asset.id)),
        ];
        cursor = response.data.nextCursor;
      }
      setAssets(collected);
      setNextCursor(cursor);
      setSelected(new Set(collected.map((asset) => asset.id)));
    } catch {
      setAlert('All favorites could not be selected. Visible items remain available.');
    } finally {
      setSelectingAll(false);
    }
  }

  async function removeFavorites() {
    const chosen = assets.filter((asset) => selected.has(asset.id));
    if (chosen.length === 0) {
      return;
    }

    setBulkPending(true);
    setAlert('');
    setResults([]);
    setAssets((current) =>
      current.filter((asset) => !selected.has(asset.id)),
    );
    setSelected(new Set());

    const outcomes = await Promise.all(
      chosen.map(async (asset): Promise<BulkResult> => {
        try {
          await client.unfavoriteAsset(asset.id, {
            ifMatch: versionTag(asset.version),
            idempotencyKey: createIdempotencyKey(),
          });
          return { id: asset.id, title: asset.title, outcome: 'Removed' };
        } catch (error) {
          if (isConflict(error)) {
            try {
              const response = await client.getAsset(asset.id);
              updateAsset(response.data.asset);
            } catch {
              updateAsset(asset);
            }
            return {
              id: asset.id,
              title: asset.title,
              outcome: 'Conflict — kept favorite',
            };
          }
          updateAsset(asset);
          return {
            id: asset.id,
            title: asset.title,
            outcome: 'Failed — try again',
          };
        }
      }),
    );
    setResults(outcomes);
    setBulkPending(false);
  }

  if (state === 'loading') {
    return <p role="status">Loading favorites…</p>;
  }

  if (state === 'error') {
    return (
      <div className={styles.error} role="alert">
        <p>Favorites could not be loaded.</p>
        <button type="button" onClick={() => void load()}>
          Try again
        </button>
      </div>
    );
  }

  return (
    <section className={styles.page} aria-labelledby="favorites-heading">
      <header className={styles.header}>
        <div>
          <p className={styles.eyebrow}>Quick collection</p>
          <h1 id="favorites-heading">Favorites</h1>
        </div>
        <p>Keep frequently used images close at hand.</p>
      </header>

      {alert ? (
        <p className={styles.error} role="alert">
          {alert}
        </p>
      ) : null}

      {assets.length === 0 && results.length === 0 ? (
        <p className={styles.empty}>
          No favorites yet. Favorite an image to find it here.
        </p>
      ) : (
        <>
          <div className={styles.toolbar} aria-label="Favorite selection">
            <div className={styles.selectionButtons}>
              <button
                type="button"
                className={styles.secondary}
                onClick={() =>
                  setSelected(new Set(assets.map((asset) => asset.id)))
                }
                disabled={assets.length === 0}
              >
                Select visible
              </button>
              <button
                type="button"
                className={styles.secondary}
                onClick={() => void selectAllResults()}
                disabled={assets.length === 0 || selectingAll}
              >
                {selectingAll ? 'Selecting…' : 'Select all results'}
              </button>
              <button
                type="button"
                className={styles.secondary}
                onClick={() => setSelected(new Set())}
                disabled={selected.size === 0}
              >
                Clear selection
              </button>
            </div>
            <span aria-live="polite">{selected.size} selected</span>
            <button
              type="button"
              onClick={() => void removeFavorites()}
              disabled={selected.size === 0 || bulkPending}
            >
              {bulkPending ? 'Removing…' : 'Remove favorites'}
            </button>
          </div>

          <ul className={styles.grid}>
            {assets.map((asset) => {
              const thumbnail = asset.renditions.find(
                (rendition) => rendition.kind === 'thumb',
              );
              return (
                <li key={asset.id} className={styles.card}>
                  <label className={styles.select}>
                    <input
                      type="checkbox"
                      checked={selected.has(asset.id)}
                      onChange={(event) => {
                        setSelected((current) => {
                          const next = new Set(current);
                          if (event.target.checked) next.add(asset.id);
                          else next.delete(asset.id);
                          return next;
                        });
                      }}
                      aria-label={`Select ${asset.title}`}
                    />
                    <span className={styles.srOnly}>Select {asset.title}</span>
                  </label>
                  {thumbnail ? (
                    <img
                      src={thumbnail.path}
                      alt=""
                      width={thumbnail.width}
                      height={thumbnail.height}
                    />
                  ) : (
                    <div className={styles.placeholder} aria-hidden="true">
                      Image
                    </div>
                  )}
                  <h2>{asset.title}</h2>
                  <FavoriteButton
                    asset={asset}
                    client={client}
                    onChange={updateAsset}
                    onConflict={setAlert}
                  />
                </li>
              );
            })}
          </ul>
        </>
      )}

      {results.length > 0 ? (
        <section className={styles.results} aria-labelledby="favorite-results">
          <h2 id="favorite-results">Bulk results</h2>
          <ul aria-live="polite">
            {results.map((result) => (
              <li key={result.id}>
                {result.title}: {result.outcome}
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </section>
  );
}


function isConflict(error: unknown): boolean {
  return error instanceof VistaraApiError && error.status === 412;
}

function createIdempotencyKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `web-${Date.now()}`;
}
