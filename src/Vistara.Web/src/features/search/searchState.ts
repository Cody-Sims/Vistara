import type {
  AssetListQuery,
  AssetSort,
  AssetQueryStatus,
  SortDirection,
} from '../../api/generated';

export interface SearchState {
  readonly query: string;
  readonly sort: AssetSort;
  readonly direction: SortDirection;
  readonly statuses: readonly AssetQueryStatus[];
  readonly favorite: boolean;
  readonly capturedFrom: string;
  readonly capturedTo: string;
}

const assetSorts = new Set<AssetSort>([
  'capturedAt',
  'importedAt',
  'updatedAt',
  'title',
]);
const directions = new Set<SortDirection>(['asc', 'desc']);
const searchableStatuses = new Set<AssetQueryStatus>([
  'ready',
  'processing',
  'failed',
]);
const isoDate = /^\d{4}-\d{2}-\d{2}$/;

export const defaultSearchState: SearchState = {
  query: '',
  sort: 'capturedAt',
  direction: 'desc',
  statuses: [],
  favorite: false,
  capturedFrom: '',
  capturedTo: '',
};

function allowed<T extends string>(
  value: string | null,
  values: ReadonlySet<T>,
  fallback: T,
): T {
  return value && values.has(value as T) ? (value as T) : fallback;
}

function allowedDate(value: string | null): string {
  return value && isoDate.test(value) && !Number.isNaN(Date.parse(value))
    ? value
    : '';
}

export function parseSearchState(params: URLSearchParams): SearchState {
  const statuses = (params.get('status') ?? '')
    .split(',')
    .filter((status): status is AssetQueryStatus =>
      searchableStatuses.has(status as AssetQueryStatus),
    );

  return {
    query: (params.get('q') ?? '').trim(),
    sort: allowed(params.get('sort'), assetSorts, defaultSearchState.sort),
    direction: allowed(
      params.get('direction'),
      directions,
      defaultSearchState.direction,
    ),
    statuses: [...new Set(statuses)],
    favorite: params.get('favorite') === 'true',
    capturedFrom: allowedDate(params.get('from')),
    capturedTo: allowedDate(params.get('to')),
  };
}

export function searchStateToSearchParams(state: SearchState): URLSearchParams {
  const params = new URLSearchParams();

  if (state.query) params.set('q', state.query);
  if (state.sort !== defaultSearchState.sort) params.set('sort', state.sort);
  if (state.direction !== defaultSearchState.direction) {
    params.set('direction', state.direction);
  }
  if (state.statuses.length > 0) params.set('status', state.statuses.join(','));
  if (state.favorite) params.set('favorite', 'true');
  if (state.capturedFrom) params.set('from', state.capturedFrom);
  if (state.capturedTo) params.set('to', state.capturedTo);

  return params;
}

export function isEmptySearch(state: SearchState): boolean {
  return (
    state.query === '' &&
    state.statuses.length === 0 &&
    !state.favorite &&
    state.capturedFrom === '' &&
    state.capturedTo === ''
  );
}

export function describeFilters(state: SearchState): readonly string[] {
  const labels: string[] = [];

  if (state.query) labels.push(`“${state.query}”`);
  if (state.favorite) labels.push('Favorites only');
  for (const status of state.statuses) {
    labels.push(`Status: ${status}`);
  }
  if (state.capturedFrom) labels.push(`Captured from ${state.capturedFrom}`);
  if (state.capturedTo) labels.push(`Captured to ${state.capturedTo}`);

  return labels;
}

export function toAssetListQuery(
  state: SearchState,
  cursor?: string,
): AssetListQuery {
  return {
    limit: 60,
    ...(state.query ? { search: state.query } : {}),
    ...(state.statuses.length > 0 ? { statuses: state.statuses } : {}),
    ...(state.favorite ? { favorite: true } : {}),
    ...(state.capturedFrom
      ? { capturedFrom: `${state.capturedFrom}T00:00:00Z` }
      : {}),
    ...(state.capturedTo ? { capturedTo: `${state.capturedTo}T23:59:59Z` } : {}),
    sort: state.sort,
    direction: state.direction,
    ...(cursor ? { cursor } : {}),
  };
}
