import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import { VistaraApiError } from '../../api/generated/client';
import type { ApiResponse } from '../../api/generated/client';
import type {
  AlbumSummary,
  AssetDetail,
  AssetSummary,
  Tag,
  VersionedAssetReference,
} from '../../api/generated/models';
import { isStaleVersion, versionTag } from '../../api/versionTag';
import { StatusMessage } from '../../components';
import { useSession } from '../session';
import {
  restorableReferences,
  restoreTrashedAssets,
  outcomeForTrashStatus,
  trashAssets,
  type CurationClient,
} from './curationClient';
import {
  describeOutcome,
  summarizeCuration,
  type CurationItemResult,
  type CurationSummary,
} from './curationOutcomes';
import {
  albumStateFor,
  curationTargets,
  favoriteStateFor,
  tagStateFor,
  toVersionedReferences,
  trashableTargets,
  type SelectionState,
} from './curationTargets';
import styles from './curation.module.css';

export interface CurationActionsProps {
  readonly client: CurationClient;
  /** The assets every action applies to: a library selection or one asset. */
  readonly assets: readonly AssetSummary[];
  /** Albums the assets are known to be in, where the caller knows them. */
  readonly albumIds?: readonly string[];
  /** Overrides the session scope check, for a preview or a focused test. */
  readonly canCurate?: boolean;
  readonly onCurated?: (assets: readonly AssetSummary[]) => void;
  /**
   * The assets that reached the trash, with the versions they landed on, so a
   * caller that replaces this surface can still offer the undo.
   */
  readonly onTrashed?: (
    ids: readonly string[],
    restorable: readonly VersionedAssetReference[],
  ) => void;
  readonly onRestored?: (ids: readonly string[]) => void;
  readonly createIdempotencyKey?: () => string;
}

type Panel = 'tags' | 'albums';

interface ListState<T> {
  readonly kind: 'loading' | 'ready' | 'failed';
  readonly items: readonly T[];
}

const idle: ListState<never> = { kind: 'loading', items: [] };

function defaultKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `web-${Date.now()}`;
}

function pressed(state: SelectionState): 'true' | 'false' | 'mixed' {
  return state === 'all' ? 'true' : state === 'some' ? 'mixed' : 'false';
}

function describeSelection(state: SelectionState, total: number): string {
  switch (state) {
    case 'all':
      return total === 1 ? 'applied' : 'applied to every image';
    case 'some':
      return 'applied to some images';
    case 'none':
      return 'not applied';
    default:
      return 'membership unknown';
  }
}

function statusOf(error: unknown): number | undefined {
  return error instanceof VistaraApiError ? error.status : undefined;
}

/**
 * Applies one change to one asset. A `412` means the copy in hand is stale, so
 * the asset is read again and the change is applied once to the version the
 * API just published. Anything else is reported rather than retried.
 */
async function applyToAsset(
  client: Pick<CurationClient, 'getAsset'>,
  asset: AssetSummary,
  run: (target: AssetSummary) => Promise<ApiResponse<AssetDetail>>,
): Promise<{ result: CurationItemResult; asset?: AssetSummary }> {
  const item = (outcome: CurationItemResult['outcome']) => ({
    id: asset.id,
    title: asset.title,
    outcome,
  });

  try {
    const response = await run(asset);
    return { result: item('updated'), asset: response.data.asset };
  } catch (error) {
    if (!isStaleVersion(error)) {
      const status = statusOf(error);
      return {
        result: item(
          status === 404 ? 'notFound' : status === 409 ? 'conflict' : 'failed',
        ),
      };
    }
  }

  try {
    const fresh = (await client.getAsset(asset.id)).data.asset;
    const response = await run(fresh);
    return { result: item('refreshed'), asset: response.data.asset };
  } catch (error) {
    return { result: item(statusOf(error) === 404 ? 'notFound' : 'conflict') };
  }
}

export function CurationActions({
  albumIds,
  assets,
  canCurate,
  client,
  createIdempotencyKey = defaultKey,
  onCurated,
  onRestored,
  onTrashed,
}: CurationActionsProps) {
  const session = useSession();
  const allowed = canCurate ?? session.scopes.includes('metadata.manage');
  const targets = curationTargets(assets);
  const trashable = trashableTargets(targets);
  const panelId = useId();
  const headingId = useId();
  const reasonId = useId();
  const newTagId = useId();
  const newAlbumId = useId();

  const [panel, setPanel] = useState<Panel>();
  const [busy, setBusy] = useState(false);
  const [summary, setSummary] = useState<CurationSummary>();
  const [details, setDetails] = useState<readonly CurationItemResult[]>([]);
  const [undoable, setUndoable] = useState<readonly VersionedAssetReference[]>(
    [],
  );
  /**
   * The favourite state shown before, and after, the API answers. It is keyed
   * to the assets it was decided for, so the moment the caller hands over
   * refreshed assets the override stops applying and the data wins.
   */
  const [favoriteOverride, setFavoriteOverride] = useState<{
    readonly key: string;
    readonly value: boolean;
  }>();
  const [confirming, setConfirming] = useState(false);
  const [reason, setReason] = useState('');
  const [tagName, setTagName] = useState('');
  const [albumName, setAlbumName] = useState('');
  const [tags, setTags] = useState<ListState<Tag>>(idle);
  const [albums, setAlbums] = useState<ListState<AlbumSummary>>(idle);

  const tagsTrigger = useRef<HTMLButtonElement>(null);
  const albumsTrigger = useRef<HTMLButtonElement>(null);
  const trashTrigger = useRef<HTMLButtonElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);

  const favorite = favoriteStateFor(targets);
  const identity = targets
    .map((target) => `${target.id}:${target.version}:${target.favorite}`)
    .join('|');
  const favorited =
    favoriteOverride?.key === identity
      ? favoriteOverride.value
      : favorite === 'all';

  const report = useCallback(
    (action: string, results: readonly CurationItemResult[]) => {
      setSummary(summarizeCuration(action, results));
      setDetails(results.length > 1 ? results : []);
    },
    [],
  );

  const loadTags = useCallback(async () => {
    setTags({ kind: 'loading', items: [] });
    try {
      const response = await client.listTags({ limit: 100 });
      setTags({ kind: 'ready', items: response.data.items });
    } catch {
      setTags({ kind: 'failed', items: [] });
    }
  }, [client]);

  const loadAlbums = useCallback(async () => {
    setAlbums({ kind: 'loading', items: [] });
    try {
      const response = await client.listAlbums({ limit: 100 });
      setAlbums({ kind: 'ready', items: response.data.items });
    } catch {
      setAlbums({ kind: 'failed', items: [] });
    }
  }, [client]);

  function openPanel(next: Panel) {
    if (panel === next) {
      setPanel(undefined);
      return;
    }

    setPanel(next);
    void (next === 'tags' ? loadTags() : loadAlbums());
  }

  const closePanel = useCallback(() => {
    const trigger = panel === 'tags' ? tagsTrigger : albumsTrigger;
    setPanel(undefined);
    trigger.current?.focus();
  }, [panel]);

  useEffect(() => {
    if (panel === undefined && !confirming) {
      return;
    }

    const handle = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') {
        return;
      }

      event.preventDefault();
      if (confirming) {
        setConfirming(false);
        trashTrigger.current?.focus();
        return;
      }

      closePanel();
    };

    window.addEventListener('keydown', handle);
    return () => window.removeEventListener('keydown', handle);
  }, [closePanel, confirming, panel]);

  useEffect(() => {
    if (confirming) {
      confirmButton.current?.focus();
    }
  }, [confirming]);

  async function runOverTargets(
    action: string,
    run: (target: AssetSummary) => Promise<ApiResponse<AssetDetail>>,
  ) {
    setBusy(true);
    setUndoable([]);
    const results: CurationItemResult[] = [];
    const updated: AssetSummary[] = [];
    for (const target of targets) {
      const outcome = await applyToAsset(client, target, run);
      results.push(outcome.result);
      if (outcome.asset) {
        updated.push(outcome.asset);
      }
    }

    report(action, results);
    setBusy(false);
    onCurated?.(updated);
    return updated;
  }

  async function toggleFavorite() {
    const next = !favorited;
    setFavoriteOverride({ key: identity, value: next });
    const updated = await runOverTargets(
      next ? 'Added to favorites' : 'Removed from favorites',
      (target) =>
        next
          ? client.favoriteAsset(target.id, {
              idempotencyKey: createIdempotencyKey(),
              ifMatch: versionTag(target.version),
            })
          : client.unfavoriteAsset(target.id, {
              idempotencyKey: createIdempotencyKey(),
              ifMatch: versionTag(target.version),
            }),
    );
    setFavoriteOverride(
      updated.length === targets.length &&
        updated.every((asset) => asset.favorite === next)
        ? { key: identity, value: next }
        : undefined,
    );
  }

  async function toggleTag(value: Tag) {
    const state = tagStateFor(targets, value.id);
    const add = state !== 'all';
    await runOverTargets(
      add ? `Tagged ${value.name}` : `Removed tag ${value.name}`,
      (target) =>
        add
          ? client.addAssetTag(target.id, value.id, {
              idempotencyKey: createIdempotencyKey(),
              ifMatch: versionTag(target.version),
            })
          : client.removeAssetTag(target.id, value.id, {
              idempotencyKey: createIdempotencyKey(),
              ifMatch: versionTag(target.version),
            }),
    );
  }

  async function createAndApplyTag(event: FormEvent) {
    event.preventDefault();
    const name = tagName.trim();
    if (name.length === 0) {
      return;
    }

    setBusy(true);
    try {
      const created = await client.createTag(
        { name },
        { idempotencyKey: createIdempotencyKey() },
      );
      setTagName('');
      setTags((current) => ({
        kind: 'ready',
        items: [...current.items, created.data],
      }));
      setBusy(false);
      await toggleTag(created.data);
    } catch {
      setBusy(false);
      report(`Tagged ${name}`, [
        { id: 'tag', title: name, outcome: 'failed' },
      ]);
    }
  }

  /**
   * Album membership is one versioned change on the album, so the whole
   * selection is sent once. A stale album version is reloaded and reapplied
   * exactly once, the same recovery a single asset gets.
   */
  async function changeAlbum(value: AlbumSummary, add: boolean) {
    setBusy(true);
    setUndoable([]);
    const items = toVersionedReferences(targets);
    const call = (album: AlbumSummary) =>
      add
        ? client.addAlbumItems(
            album.id,
            { items },
            {
              idempotencyKey: createIdempotencyKey(),
              ifMatch: versionTag(album.version),
            },
          )
        : client.removeAlbumItems(
            album.id,
            { items },
            {
              idempotencyKey: createIdempotencyKey(),
              ifMatch: versionTag(album.version),
            },
          );
    const action = add ? `Added to ${value.name}` : `Removed from ${value.name}`;
    const every = (outcome: CurationItemResult['outcome']) =>
      targets.map((target) => ({
        id: target.id,
        title: target.title,
        outcome,
      }));

    let album = value;
    let updated;
    try {
      updated = (await call(album)).data.album;
    } catch (error) {
      if (!isStaleVersion(error)) {
        const status = statusOf(error);
        report(action, every(status === 404 ? 'notFound' : 'failed'));
        setBusy(false);
        return;
      }

      try {
        album = (await client.getAlbum(album.id)).data.album;
        updated = (await call(album)).data.album;
      } catch {
        report(action, every('conflict'));
        setBusy(false);
        return;
      }
    }

    const covered = add ? await ensureCover(album, updated) : updated;
    setAlbums((current) => ({
      kind: current.kind,
      items: current.items.map((item) =>
        item.id === covered.id ? covered : item,
      ),
    }));
    report(action, every('updated'));
    setBusy(false);
    onCurated?.([]);
  }

  /**
   * A new album has no cover, so the first image added to it becomes one.
   * A failure here never fails the change the person asked for.
   */
  async function ensureCover(previous: AlbumSummary, updated: AlbumSummary) {
    const candidate = targets.find(
      (target) => target.status === 'ready' && target.renditions.length > 0,
    );
    if (previous.cover || updated.cover || !candidate) {
      return updated;
    }

    try {
      const response = await client.updateAlbum(
        updated.id,
        { coverAssetId: candidate.id },
        {
          idempotencyKey: createIdempotencyKey(),
          ifMatch: versionTag(updated.version),
        },
      );
      return response.data.album;
    } catch {
      return updated;
    }
  }

  async function createAndAddAlbum(event: FormEvent) {
    event.preventDefault();
    const name = albumName.trim();
    if (name.length === 0) {
      return;
    }

    setBusy(true);
    try {
      const created = await client.createAlbum(
        { name },
        { idempotencyKey: createIdempotencyKey() },
      );
      setAlbumName('');
      const album = created.data.album;
      setAlbums((current) => ({
        kind: 'ready',
        items: [...current.items, album],
      }));
      setBusy(false);
      await changeAlbum(album, true);
    } catch {
      setBusy(false);
      report(`Added to ${name}`, [
        { id: 'album', title: name, outcome: 'failed' },
      ]);
    }
  }

  async function confirmTrash() {
    setBusy(true);
    setConfirming(false);
    const answer = await trashAssets(
      client,
      toVersionedReferences(trashable),
      reason,
      createIdempotencyKey(),
    ).catch(() => undefined);
    setReason('');

    if (!answer) {
      report(
        'Moved to trash',
        trashable.map((target) => ({
          id: target.id,
          title: target.title,
          outcome: 'failed' as const,
        })),
      );
      setBusy(false);
      trashTrigger.current?.focus();
      return;
    }

    if (answer.kind === 'queued') {
      setSummary({
        message: `Move to trash queued for ${answer.job.submittedCount} images.`,
        tone: 'success',
      });
      setDetails([]);
      setBusy(false);
      onTrashed?.(trashable.map((target) => target.id), []);
      return;
    }

    const titles = new Map(trashable.map((target) => [target.id, target.title]));
    const results = answer.results.map((result) => ({
      id: result.assetId,
      title: titles.get(result.assetId) ?? result.assetId,
      outcome: outcomeForTrashStatus(result.status),
    }));
    const restorable = restorableReferences(answer.results);
    report('Moved to trash', results);
    setUndoable(restorable);
    setBusy(false);
    onTrashed?.(
      results
        .filter((result) => result.outcome === 'updated')
        .map((result) => result.id),
      restorable,
    );
  }

  async function undoTrash() {
    setBusy(true);
    const items = undoable;
    try {
      const job = await restoreTrashedAssets(
        client,
        items,
        createIdempotencyKey(),
      );
      setUndoable([]);
      setDetails([]);
      setSummary({
        message: `Restore queued for ${
          job.submittedCount === 1 ? '1 image' : `${job.submittedCount} images`
        }.`,
        tone: 'success',
      });
      onRestored?.(items.map((item) => item.id));
    } catch {
      setSummary({
        message: 'The restore could not be started. Open Trash to try again.',
        tone: 'danger',
      });
    } finally {
      setBusy(false);
    }
  }

  if (!allowed || targets.length === 0) {
    return null;
  }

  return (
    <section
      aria-busy={busy}
      aria-label="Curation actions"
      className={styles.bar}
      role="group"
    >
      <p className={styles.count}>
        {targets.length === 1
          ? '1 image selected'
          : `${targets.length} images selected`}
      </p>

      <div className={styles.controls}>
        <button
          aria-pressed={favorited}
          className={styles.action}
          disabled={busy}
          onClick={() => void toggleFavorite()}
          type="button"
        >
          <span aria-hidden="true">{favorited ? '★' : '☆'}</span>
          {favorited ? 'Favorited' : 'Favorite'}
        </button>
        <button
          aria-controls={`${panelId}-tags`}
          aria-expanded={panel === 'tags'}
          className={styles.action}
          disabled={busy}
          onClick={() => openPanel('tags')}
          ref={tagsTrigger}
          type="button"
        >
          Tags
        </button>
        <button
          aria-controls={`${panelId}-albums`}
          aria-expanded={panel === 'albums'}
          className={styles.action}
          disabled={busy}
          onClick={() => openPanel('albums')}
          ref={albumsTrigger}
          type="button"
        >
          Albums
        </button>
        <button
          className={styles.danger}
          disabled={busy || trashable.length === 0}
          onClick={() => setConfirming(true)}
          ref={trashTrigger}
          type="button"
        >
          Move to trash
        </button>
      </div>

      {panel === 'tags' ? (
        <div
          aria-label="Tags"
          className={styles.panel}
          id={`${panelId}-tags`}
          role="group"
        >
          {tags.kind === 'loading' ? (
            <StatusMessage live tone="pending" title="Loading tags…" />
          ) : null}
          {tags.kind === 'failed' ? (
            <StatusMessage
              action={
                <button
                  className={styles.action}
                  onClick={() => void loadTags()}
                  type="button"
                >
                  Try again
                </button>
              }
              headingLevel={3}
              title="Tags could not be loaded"
              tone="danger"
            />
          ) : null}
          {tags.kind === 'ready' && tags.items.length === 0 ? (
            <StatusMessage
              description="Name one below to tag this selection."
              headingLevel={3}
              title="No tags yet"
              tone="empty"
            />
          ) : null}
          {tags.items.length > 0 ? (
            <ul className={styles.options}>
              {tags.items.map((value) => {
                const state = tagStateFor(targets, value.id);
                return (
                  <li key={value.id}>
                    <button
                      aria-pressed={pressed(state)}
                      className={styles.option}
                      disabled={busy}
                      onClick={() => void toggleTag(value)}
                      type="button"
                    >
                      {value.name}
                      <span className={styles.srOnly}>
                        {` ${describeSelection(state, targets.length)}`}
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : null}
          <form className={styles.createForm} onSubmit={createAndApplyTag}>
            <label htmlFor={newTagId}>New tag name</label>
            <input
              id={newTagId}
              maxLength={64}
              onChange={(event) => setTagName(event.target.value)}
              type="text"
              value={tagName}
            />
            <button className={styles.action} disabled={busy} type="submit">
              Create and add
            </button>
          </form>
        </div>
      ) : null}

      {panel === 'albums' ? (
        <div
          aria-label="Albums"
          className={styles.panel}
          id={`${panelId}-albums`}
          role="group"
        >
          {albums.kind === 'loading' ? (
            <StatusMessage live tone="pending" title="Loading albums…" />
          ) : null}
          {albums.kind === 'failed' ? (
            <StatusMessage
              action={
                <button
                  className={styles.action}
                  onClick={() => void loadAlbums()}
                  type="button"
                >
                  Try again
                </button>
              }
              headingLevel={3}
              title="Albums could not be loaded"
              tone="danger"
            />
          ) : null}
          {albums.kind === 'ready' && albums.items.length === 0 ? (
            <StatusMessage
              description="Name one below to start organizing."
              headingLevel={3}
              title="No albums yet"
              tone="empty"
            />
          ) : null}
          {albums.items.length > 0 ? (
            <ul className={styles.options}>
              {albums.items.map((value) => {
                const state = albumStateFor(
                  targets.map((target) => ({
                    id: target.id,
                    ...(albumIds ? { albumIds } : {}),
                  })),
                  value.id,
                );
                return (
                  <li className={styles.albumRow} key={value.id}>
                    <span className={styles.albumName}>{value.name}</span>
                    <button
                      className={styles.option}
                      disabled={busy || state === 'all'}
                      onClick={() => void changeAlbum(value, true)}
                      type="button"
                    >
                      {`Add to ${value.name}`}
                    </button>
                    <button
                      className={styles.option}
                      disabled={busy || state === 'none'}
                      onClick={() => void changeAlbum(value, false)}
                      type="button"
                    >
                      {`Remove from ${value.name}`}
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : null}
          <form className={styles.createForm} onSubmit={createAndAddAlbum}>
            <label htmlFor={newAlbumId}>New album name</label>
            <input
              id={newAlbumId}
              maxLength={120}
              onChange={(event) => setAlbumName(event.target.value)}
              type="text"
              value={albumName}
            />
            <button className={styles.action} disabled={busy} type="submit">
              Create and add
            </button>
          </form>
        </div>
      ) : null}

      {confirming ? (
        <div
          aria-labelledby={headingId}
          aria-modal="true"
          className={styles.confirm}
          role="dialog"
        >
          <h3 id={headingId}>
            {trashable.length === 1
              ? 'Move 1 image to trash?'
              : `Move ${trashable.length} images to trash?`}
          </h3>
          <p>
            Trashed images leave the library and can be restored from Trash
            until they are purged.
          </p>
          <label htmlFor={reasonId}>Reason (optional)</label>
          <input
            id={reasonId}
            maxLength={200}
            onChange={(event) => setReason(event.target.value)}
            type="text"
            value={reason}
          />
          <div className={styles.confirmActions}>
            <button
              className={styles.danger}
              onClick={() => void confirmTrash()}
              ref={confirmButton}
              type="button"
            >
              Move to trash
            </button>
            <button
              className={styles.action}
              onClick={() => {
                setConfirming(false);
                trashTrigger.current?.focus();
              }}
              type="button"
            >
              Keep images
            </button>
          </div>
        </div>
      ) : null}

      <div className={styles.report}>
        <p
          aria-live="polite"
          className={styles.summary}
          data-tone={summary?.tone}
          role="status"
        >
          {summary?.message ?? ''}
        </p>
        {undoable.length > 0 ? (
          <button
            className={styles.action}
            disabled={busy}
            onClick={() => void undoTrash()}
            type="button"
          >
            Undo move to trash
          </button>
        ) : null}
        {details.length > 0 ? (
          <ul aria-label="Result for each image" className={styles.details}>
            {details.map((result) => (
              <li key={result.id}>
                <span>{result.title}</span>
                <span className={styles.outcome}>
                  {describeOutcome(result.outcome)}
                </span>
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    </section>
  );
}
