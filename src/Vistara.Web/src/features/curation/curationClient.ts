import type { VistaraApiClient } from '../../api/generated/client';
import type {
  AssetBulkAction,
  OperationJob,
  VersionedAssetReference,
} from '../../api/generated/models';
import type { CurationOutcome } from './curationOutcomes';

/**
 * The gallery routes this surface spends. Everything is taken from the
 * generated client; nothing here reaches a route the API does not publish.
 */
export type CurationClient = Pick<
  VistaraApiClient,
  | 'getAsset'
  | 'favoriteAsset'
  | 'unfavoriteAsset'
  | 'addAssetTag'
  | 'removeAssetTag'
  | 'listTags'
  | 'createTag'
  | 'listAlbums'
  | 'getAlbum'
  | 'createAlbum'
  | 'updateAlbum'
  | 'addAlbumItems'
  | 'removeAlbumItems'
  | 'bulkMutateAssets'
  | 'restoreAssets'
>;

/**
 * `POST /api/v1/assets/bulk` also carries the lifecycle trash action, which
 * answers `202` with one result per asset instead of a queued job. The
 * generated models describe only the curation actions the published contract
 * lists, so the request and the answer are named here and validated at
 * runtime. Nothing is assumed about a deployment that answers with a job: it
 * is reported as queued rather than as per-asset results.
 *
 * Request  `{ items: [{ id, version }], action: { kind: 'trash', reason } }`
 * Answer   `[{ assetId, status, version?, errorCode? }]`
 * Statuses `trashed | versionConflict | notFound | invalidState`
 *
 * The API folds `alreadyTrashed` into `trashed` and `forbidden` into
 * `notFound` before it answers, and omits `version` whenever it sends an
 * `errorCode`. `alreadyTrashed` is still mapped here so a deployment that
 * publishes it is read as an image that is already where it was asked to be.
 */
export interface AssetMutationResult {
  readonly assetId: string;
  readonly status: string;
  readonly version?: number;
  readonly errorCode?: string;
}

export type TrashAnswer =
  | { readonly kind: 'results'; readonly results: readonly AssetMutationResult[] }
  | { readonly kind: 'queued'; readonly job: OperationJob };

/** The API refuses a blank reason, so a plain action still explains itself. */
export const defaultTrashReason = 'Moved to trash from the gallery';

function trashAction(reason: string): AssetBulkAction {
  const trimmed = reason.trim();
  return {
    kind: 'trash',
    reason: trimmed.length > 0 ? trimmed : defaultTrashReason,
  } as unknown as AssetBulkAction;
}

function isMutationResult(value: unknown): value is AssetMutationResult {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as AssetMutationResult).assetId === 'string' &&
    typeof (value as AssetMutationResult).status === 'string'
  );
}

export async function trashAssets(
  client: Pick<CurationClient, 'bulkMutateAssets'>,
  items: readonly VersionedAssetReference[],
  reason: string,
  idempotencyKey: string,
): Promise<TrashAnswer> {
  const response = await client.bulkMutateAssets(
    { items, action: trashAction(reason) },
    { idempotencyKey },
  );
  const data: unknown = response.data;

  return Array.isArray(data) && data.every(isMutationResult)
    ? { kind: 'results', results: data }
    : { kind: 'queued', job: response.data };
}

export function outcomeForTrashStatus(status: string): CurationOutcome {
  switch (status) {
    case 'trashed':
      return 'updated';
    case 'alreadyTrashed':
      return 'unchanged';
    case 'versionConflict':
      return 'conflict';
    case 'notFound':
      return 'notFound';
    default:
      return 'failed';
  }
}

/** Only an asset that reached the trash, with the version it landed on. */
export function restorableReferences(
  results: readonly AssetMutationResult[],
): readonly VersionedAssetReference[] {
  return results
    .filter(
      (result) =>
        (result.status === 'trashed' || result.status === 'alreadyTrashed') &&
        result.version !== undefined,
    )
    .map((result) => ({ id: result.assetId, version: result.version! }));
}

/** The API accepts at most this many references in one batch. */
export const maximumBatch = 200;

export function batches<T>(items: readonly T[]): readonly (readonly T[])[] {
  const chunks: T[][] = [];
  for (let index = 0; index < items.length; index += maximumBatch) {
    chunks.push(items.slice(index, index + maximumBatch));
  }

  return chunks.length > 0 ? chunks : [[]];
}

/**
 * Restores in batches the API accepts, answering one job per batch. Callers
 * add the submitted counts up rather than reading one job.
 */
export async function restoreTrashedAssets(
  client: Pick<CurationClient, 'restoreAssets'>,
  items: readonly VersionedAssetReference[],
  createIdempotencyKey: () => string,
): Promise<readonly OperationJob[]> {
  const jobs: OperationJob[] = [];
  for (const batch of batches(items)) {
    const response = await client.restoreAssets(
      { items: batch },
      { idempotencyKey: createIdempotencyKey() },
    );
    jobs.push(response.data);
  }

  return jobs;
}
