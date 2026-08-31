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
  AssetBulkAction,
  AssetDetail,
  AssetSummary,
  Tag,
  VersionedAssetReference,
} from '../../api/generated/models';
import { useDialogFocusTrap } from '../../accessibility/focus';
import { isStaleVersion, versionTag } from '../../api/versionTag';
import { StatusMessage } from '../../components';
import { useSession } from '../session';
import { retryAfterSeconds } from '../../api/throttling';
import type { AssetMutationResult } from './curationClient';
import {
  batches,
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
  /** Waits out a rate limit; replaced in tests so they do not really wait. */
  readonly wait?: (
    milliseconds: number,
    signal: AbortSignal,
  ) => Promise<void>;
}

type Panel = 'tags' | 'albums';

interface ListState<T> {
  readonly kind: 'loading' | 'ready' | 'failed';
  readonly items: readonly T[];
}

const idle: ListState<never> = { kind: 'loading', items: [] };

/** A refused batch is retried once, after the delay the API asked for. */
const maximumThrottleWait = 30_000;

/**
 * A replayed create answers with the record that already exists, so it
 * replaces the copy in the list instead of joining it a second time.
 */
function withRecord<T extends { readonly id: string }>(
  items: readonly T[],
  record: T,
): readonly T[] {
  return items.some((item) => item.id === record.id)
    ? items.map((item) => (item.id === record.id ? record : item))
    : [...items, record];
}

/** How many reads of a stale version are in flight at once. */
const concurrentVersionReads = 4;

function sleep(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }

    const finish = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', finish);
      resolve();
    };
    const timer = setTimeout(finish, milliseconds);
    signal.addEventListener('abort', finish);
  });
}

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
  wait = sleep,
}: CurationActionsProps) {
  const session = useSession();
  const allowed = canCurate ?? session.scopes.includes('metadata.manage');
  const targets = curationTargets(assets);
  const trashable = trashableTargets(targets);
  /** Assets that already left the library cannot be curated any further. */
  const actionable = targets.filter(
    (target) => target.status !== 'trashed' && target.status !== 'purged',
  );
  const panelId = useId();
  const headingId = useId();
  const reasonId = useId();
  const newTagId = useId();
  const newAlbumId = useId();

  const [panel, setPanel] = useState<Panel>();
  const [busy, setBusy] = useState(false);
  /**
   * What the last action did, tied to the assets it was asked about. A refresh
   * that only changes versions keeps it; a different selection retires it.
   */
  const [outcome, setOutcome] = useState<{
    readonly key: string;
    readonly summary: CurationSummary;
    readonly details: readonly CurationItemResult[];
    readonly undoable: readonly VersionedAssetReference[];
  }>();
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

  /** Set while a rate limit is being waited out, so it can be stopped. */
  const [waitingSeconds, setWaitingSeconds] = useState<number>();
  const stopWaiting = useRef<AbortController>(undefined);
  /** Bumped when a finished action should take focus to its report. */
  const [focusReport, setFocusReport] = useState(0);
  const tagsTrigger = useRef<HTMLButtonElement>(null);
  const albumsTrigger = useRef<HTMLButtonElement>(null);
  const trashTrigger = useRef<HTMLButtonElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  const confirmDialog = useRef<HTMLDivElement>(null);
  const undoButton = useRef<HTMLButtonElement>(null);
  const summaryRef = useRef<HTMLParagraphElement>(null);

  const scope = targets.map((target) => target.id).join('|');
  /**
   * The outcome stays while any image it describes is still in hand. A partial
   * trash leaves the images it could not move selected, and their result, and
   * the undo for the ones that did move, belong with them.
   */
  const inScope = new Set(targets.map((target) => target.id));
  const shown =
    outcome && outcome.key.split('|').some((id) => inScope.has(id))
      ? outcome
      : undefined;
  const summary = shown?.summary;
  const details = shown?.details ?? [];
  const undoable = shown?.undoable ?? [];
  const favorite = favoriteStateFor(targets);
  const identity = targets
    .map((target) => `${target.id}:${target.version}:${target.favorite}`)
    .join('|');
  const favorited =
    favoriteOverride?.key === identity
      ? favoriteOverride.value
      : favorite === 'all';

  function report(
    action: string,
    results: readonly CurationItemResult[],
    undo: readonly VersionedAssetReference[] = [],
  ) {
    setOutcome({
      key: scope,
      summary: summarizeCuration(action, results),
      details: results.length > 1 ? results : [],
      undoable: undo,
    });
  }

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
    if (panel === undefined) {
      return;
    }

    const handle = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') {
        return;
      }

      // Marked handled before it reaches a page that also listens for Escape.
      event.preventDefault();
      closePanel();
    };

    document.addEventListener('keydown', handle, true);
    return () => document.removeEventListener('keydown', handle, true);
  }, [closePanel, panel]);

  // The trap restores focus to whatever opened the confirmation, so dismissing
  // it only has to close it.
  const dismissConfirm = useCallback(() => setConfirming(false), []);

  useDialogFocusTrap({
    dialogRef: confirmDialog,
    initialFocusRef: confirmButton,
    onDismiss: dismissConfirm,
    open: confirming,
  });

  useEffect(() => {
    if (focusReport === 0) {
      return;
    }

    (undoButton.current ?? summaryRef.current)?.focus();
  }, [focusReport]);

  /**
   * One attempt, then — if the API asked the caller to slow down — one more
   * after the delay it named. The wait can be stopped, which abandons the
   * retry and tells the caller nothing further should be attempted.
   */
  async function withThrottleRetry<T>(
    run: () => Promise<T>,
    signal: AbortSignal,
  ): Promise<
    | { readonly kind: 'value'; readonly value: T }
    | { readonly kind: 'failed'; readonly error: unknown }
    | { readonly kind: 'throttled'; readonly error: unknown }
  > {
    try {
      return { kind: 'value', value: await run() };
    } catch (error) {
      const seconds = retryAfterSeconds(error);
      if (seconds === undefined) {
        return { kind: 'failed', error };
      }

      setWaitingSeconds(seconds);
      try {
        await wait(Math.min(seconds * 1000, maximumThrottleWait), signal);
      } finally {
        setWaitingSeconds(undefined);
      }

      if (signal.aborted) {
        return { kind: 'throttled', error };
      }
    }

    try {
      return { kind: 'value', value: await run() };
    } catch (error) {
      return retryAfterSeconds(error) === undefined
        ? { kind: 'failed', error }
        : { kind: 'throttled', error };
    }
  }

  /** Runs an action with a controller the visitor can abort. */
  function startRun(): AbortController {
    const controller = new AbortController();
    stopWaiting.current = controller;
    return controller;
  }

  function endRun() {
    stopWaiting.current = undefined;
    setWaitingSeconds(undefined);
  }

  /**
   * Applies a versioned change to each target in turn. A selection larger than
   * one image goes through the bulk route instead, so this carries the single
   * asset a viewer, or a one-image selection, holds.
   */
  async function runOverTargets(
    action: string,
    run: (target: AssetSummary) => Promise<ApiResponse<AssetDetail>>,
  ) {
    setBusy(true);
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

  /**
   * Sends one action for the whole selection through the bulk route, in
   * batches the API accepts. The route answers with a queued job rather than
   * per-asset results, so every image in an accepted batch is reported as
   * queued and every image in a refused one is reported with what refused it.
   * A `429` is retried once, after the delay the answer asked for, so a rate
   * limit is respected instead of hammered.
   */
  async function runBulk(action: string, bulkAction: AssetBulkAction) {
    setBusy(true);
    const controller = startRun();
    const results: CurationItemResult[] = [];
    const chunks = batches(actionable);
    let refused = false;
    let stopped = false;

    for (const batch of chunks) {
      const item = (
        target: AssetSummary,
        outcome: CurationItemResult['outcome'],
      ) => ({ id: target.id, title: target.title, outcome });

      if (stopped) {
        results.push(...batch.map((target) => item(target, 'untouched')));
        continue;
      }

      const answer = await withThrottleRetry(
        () =>
          client.bulkMutateAssets(
            { items: toVersionedReferences(batch), action: bulkAction },
            { idempotencyKey: createIdempotencyKey() },
          ),
        controller.signal,
      );


      if (answer.kind === 'value') {
        results.push(...batch.map((target) => item(target, 'queued')));
        continue;
      }

      refused = true;
      if (answer.kind === 'throttled') {
        /**
         * The API is still refusing after the delay it asked for, or the wait
         * was stopped. Sending the batches behind this one would only repeat
         * the wait, so they are left alone and said to be.
         */
        stopped = true;
        const attempted = controller.signal.aborted ? 'untouched' : 'failed';
        results.push(...batch.map((target) => item(target, attempted)));
        continue;
      }

      const status = statusOf(answer.error);
      results.push(
        ...batch.map((target) =>
          item(
            target,
            status === 404
              ? 'notFound'
              : status === 409 || status === 412
                ? 'conflict'
                : 'failed',
          ),
        ),
      );
    }

    endRun();
    report(action, results);
    setBusy(false);
    onCurated?.([]);
    return !refused;
  }

  async function toggleFavorite() {
    const next = !favorited;
    setFavoriteOverride({ key: identity, value: next });
    const action = next ? 'Added to favorites' : 'Removed from favorites';

    if (actionable.length > 1) {
      const accepted = await runBulk(action, {
        kind: 'setFavorite',
        favorite: next,
      });
      setFavoriteOverride(accepted ? { key: identity, value: next } : undefined);
      return;
    }

    const updated = await runOverTargets(action, (target) =>
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
    const action = add ? `Tagged ${value.name}` : `Removed tag ${value.name}`;

    if (actionable.length > 1) {
      await runBulk(
        action,
        add
          ? { kind: 'addTag', tagId: value.id }
          : { kind: 'removeTag', tagId: value.id },
      );
      return;
    }

    await runOverTargets(action, (target) =>
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
        items: withRecord(current.items, created.data),
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
    const controller = startRun();
    const action = add ? `Added to ${value.name}` : `Removed from ${value.name}`;
    const call = (
      album: AlbumSummary,
      items: readonly VersionedAssetReference[],
    ) =>
      (add ? client.addAlbumItems : client.removeAlbumItems).call(
        client,
        album.id,
        { items },
        {
          idempotencyKey: createIdempotencyKey(),
          ifMatch: versionTag(album.version),
        },
      );

    let album = value;
    let committedAny = false;
    let stopped = false;
    const results: CurationItemResult[] = [];
    const versions: AssetSummary[] = [];

    for (const batch of batches(toVersionedReferences(actionable))) {
      const inBatch = batch.map(
        (item) => actionable.find((target) => target.id === item.id)!,
      );
      const outcome = (
        value: CurationItemResult['outcome'],
        only?: ReadonlySet<string>,
      ) =>
        inBatch
          .filter((target) => only === undefined || only.has(target.id))
          .map((target) => ({
            id: target.id,
            title: target.title,
            outcome: value,
          }));

      if (stopped) {
        results.push(...outcome('untouched'));
        continue;
      }

      let detail;
      let unreadable: ReadonlySet<string> = new Set();
      const first = await withThrottleRetry(
        () => call(album, batch),
        controller.signal,
      );

      if (first.kind === 'value') {
        detail = first.value.data;
      } else if (first.kind === 'throttled') {
        stopped = true;
        results.push(
          ...outcome(controller.signal.aborted ? 'untouched' : 'failed'),
        );
        continue;
      } else if (!isStaleVersion(first.error)) {
        const status = statusOf(first.error);
        results.push(...outcome(status === 404 ? 'notFound' : 'failed'));
        continue;
      } else {
        /**
         * `412` answers a stale `If-Match` on the album and a stale version on
         * any asset in the batch, so both are read again before the single
         * retry: reusing the versions in hand would fail the same way.
         */
        const reload = await withThrottleRetry(
          () => client.getAlbum(album.id),
          controller.signal,
        );
        if (reload.kind !== 'value') {
          stopped = reload.kind === 'throttled';
          results.push(
            ...outcome(
              controller.signal.aborted ? 'untouched' : 'conflict',
            ),
          );
          continue;
        }

        album = reload.value.data.album;
        const fresh = await refreshed(batch, controller.signal);
        unreadable = new Set(fresh.unreadable);
        if (fresh.items.length === 0) {
          results.push(...outcome('conflict'));
          continue;
        }

        const retry = await withThrottleRetry(
          () => call(album, fresh.items),
          controller.signal,
        );
        if (retry.kind !== 'value') {
          stopped = retry.kind === 'throttled';
          results.push(
            ...outcome(controller.signal.aborted ? 'untouched' : 'conflict'),
          );
          continue;
        }

        detail = retry.value.data;
      }

      // A batch the API took is committed, whatever a later one does.
      committedAny = true;
      album = detail.album;
      const applied = new Set(
        inBatch
          .map((target) => target.id)
          .filter((id) => !unreadable.has(id)),
      );
      results.push(...outcome('updated', applied));
      results.push(...outcome('conflict', unreadable));
      for (const item of detail.items.items) {
        if (applied.has(item.asset.id)) {
          versions.push(item.asset);
        }
      }
    }

    const covered =
      add && committedAny ? await ensureCover(value, album) : album;
    if (committedAny) {
      setAlbums((current) => ({
        kind: current.kind,
        items: current.items.map((item) =>
          item.id === covered.id ? covered : item,
        ),
      }));
    }

    endRun();
    report(action, results);
    setBusy(false);
    if (committedAny) {
      onCurated?.(versions);
    }
  }

  /**
   * Reads the version each asset is on now. A stale album batch can hold two
   * hundred references, so the reads are bounded and each one follows the same
   * rate-limit discipline as everything else: one retry after the delay the
   * API asked for. Anything still unreadable is named, so the caller reports
   * it rather than quietly leaving it out.
   */
  async function refreshed(
    items: readonly VersionedAssetReference[],
    signal: AbortSignal,
  ): Promise<{
    readonly items: readonly VersionedAssetReference[];
    readonly unreadable: readonly string[];
  }> {
    const current: (VersionedAssetReference | undefined)[] = new Array(
      items.length,
    );
    let next = 0;
    const worker = async () => {
      for (let index = next++; index < items.length; index = next++) {
        const item = items[index]!;
        const answer = await withThrottleRetry(
          () => client.getAsset(item.id),
          signal,
        );
        current[index] =
          answer.kind === 'value'
            ? {
                id: answer.value.data.asset.id,
                version: answer.value.data.asset.version,
              }
            : undefined;
      }
    };

    await Promise.all(
      Array.from(
        { length: Math.min(concurrentVersionReads, items.length) },
        worker,
      ),
    );

    return {
      items: current.filter(
        (item): item is VersionedAssetReference => item !== undefined,
      ),
      unreadable: items
        .filter((_, index) => current[index] === undefined)
        .map((item) => item.id),
    };
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
        items: withRecord(current.items, album),
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
    const items = toVersionedReferences(trashable);
    const titles = new Map(trashable.map((target) => [target.id, target.title]));
    const answers: AssetMutationResult[] = [];
    let queued = 0;

    for (const batch of batches(items)) {
      try {
        const answer = await trashAssets(
          client,
          batch,
          reason,
          createIdempotencyKey(),
        );
        if (answer.kind === 'queued') {
          queued += answer.job.submittedCount;
        } else {
          answers.push(...answer.results);
        }
      } catch {
        for (const item of batch) {
          answers.push({ assetId: item.id, status: 'failed' });
        }
      }
    }

    setReason('');
    if (queued > 0 && answers.length === 0) {
      setOutcome({
        key: scope,
        summary: {
          message: `Move to trash queued for ${
            queued === 1 ? '1 image' : `${queued} images`
          }.`,
          tone: 'success',
        },
        details: [],
        undoable: [],
      });
      setBusy(false);
      onTrashed?.(
        trashable.map((target) => target.id),
        [],
      );
      return;
    }

    const results = answers.map((result) => ({
      id: result.assetId,
      title: titles.get(result.assetId) ?? result.assetId,
      outcome: outcomeForTrashStatus(result.status),
    }));
    const restorable = restorableReferences(answers);
    report('Moved to trash', results, restorable);
    setBusy(false);
    const gone = results
      .filter((result) => result.outcome === 'updated')
      .map((result) => result.id);
    onTrashed?.(gone, restorable);
    if (gone.length > 0) {
      setFocusReport((current) => current + 1);
    }
  }

  async function undoTrash() {
    setBusy(true);
    const items = undoable;
    try {
      const jobs = await restoreTrashedAssets(
        client,
        items,
        createIdempotencyKey,
      );
      const submitted = jobs.reduce(
        (total, job) => total + job.submittedCount,
        0,
      );
      setOutcome({
        key: scope,
        summary: {
          message: `Restore queued for ${
            submitted === 1 ? '1 image' : `${submitted} images`
          }.`,
          tone: 'success',
        },
        details: [],
        undoable: [],
      });
      onRestored?.(items.map((item) => item.id));
    } catch {
      setOutcome({
        key: scope,
        summary: {
          message: 'The restore could not be started. Open Trash to try again.',
          tone: 'danger',
        },
        details: [],
        undoable: [],
      });
    } finally {
      setBusy(false);
    }
  }

  const reportNode = (
    <div className={styles.report}>
      {waitingSeconds === undefined ? null : (
        <div className={styles.waiting}>
          <p
            aria-label="Curation progress"
            aria-live="polite"
            className={styles.summary}
            data-tone="warning"
            role="status"
          >
            {`Waiting ${
              waitingSeconds === 1 ? '1 second' : `${waitingSeconds} seconds`
            } because the server asked Vistara to slow down.`}
          </p>
          <button
            className={styles.action}
            onClick={() => stopWaiting.current?.abort()}
            type="button"
          >
            Stop waiting
          </button>
        </div>
      )}
      <p
        aria-label="Curation result"
        aria-live="polite"
        className={styles.summary}
        data-tone={summary?.tone}
        ref={summaryRef}
        role="status"
        tabIndex={-1}
      >
        {summary?.message ?? ''}
      </p>
      {undoable.length > 0 ? (
        <button
          className={styles.action}
          disabled={busy}
          onClick={() => void undoTrash()}
          ref={undoButton}
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
  );

  if (!allowed || targets.length === 0) {
    return null;
  }

  /**
   * Once every asset has left the library there is nothing left to act on, so
   * the controls go, but what happened to them, and the undo, stay. The report
   * keeps its place in the tree either way, so the live region is updated
   * rather than replaced with its message already in it.
   */
  const controls = actionable.length === 0 ? null : (
    <>
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
          ref={confirmDialog}
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
              onClick={dismissConfirm}
              type="button"
            >
              Keep images
            </button>
          </div>
        </div>
      ) : null}

    </>
  );

  return (
    <section
      aria-busy={busy}
      aria-label="Curation actions"
      className={styles.bar}
      role="group"
    >
      {controls}
      {reportNode}
    </section>
  );
}
