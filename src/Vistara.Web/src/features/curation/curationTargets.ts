import type { AssetSummary, VersionedAssetReference } from '../../api/generated';

/**
 * How a property applies across the whole selection. `unknown` is only used
 * where the timeline does not publish the relationship, so the interface says
 * so instead of guessing.
 */
export type SelectionState = 'none' | 'some' | 'all' | 'unknown';

/** An asset the interface knows the album membership of, such as the viewer. */
export interface AlbumMembership {
  readonly id: string;
  readonly albumIds?: readonly string[];
}

/** Statuses the API accepts for a move to the trash. */
const trashableStatuses = new Set(['ready']);

export function curationTargets(
  assets: readonly AssetSummary[],
): readonly AssetSummary[] {
  const seen = new Set<string>();
  return assets.filter((asset) => {
    if (seen.has(asset.id)) {
      return false;
    }

    seen.add(asset.id);
    return true;
  });
}

export function toVersionedReferences(
  assets: readonly AssetSummary[],
): readonly VersionedAssetReference[] {
  return assets.map((asset) => ({ id: asset.id, version: asset.version }));
}

function stateOf(total: number, matches: number): SelectionState {
  if (total === 0 || matches === 0) {
    return 'none';
  }

  return matches === total ? 'all' : 'some';
}

export function favoriteStateFor(
  assets: readonly AssetSummary[],
): SelectionState {
  return stateOf(
    assets.length,
    assets.filter((asset) => asset.favorite).length,
  );
}

export function tagStateFor(
  assets: readonly AssetSummary[],
  tagId: string,
): SelectionState {
  return stateOf(
    assets.length,
    assets.filter((asset) => asset.tags.some((tag) => tag.id === tagId)).length,
  );
}

export function albumStateFor(
  assets: readonly AlbumMembership[],
  albumId: string,
): SelectionState {
  if (assets.some((asset) => asset.albumIds === undefined)) {
    return 'unknown';
  }

  return stateOf(
    assets.length,
    assets.filter((asset) => asset.albumIds?.includes(albumId)).length,
  );
}

export function trashableTargets(
  assets: readonly AssetSummary[],
): readonly AssetSummary[] {
  return assets.filter((asset) => trashableStatuses.has(asset.status));
}
