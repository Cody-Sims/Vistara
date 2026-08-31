import type {
  AssetQueryStatus,
  AssetSort,
  SortDirection,
  TimelineGrouping,
} from '../../api/generated';

export type LibraryView = 'grid' | 'list';

export interface LibraryState {
  search: string;
  sort: AssetSort;
  direction: SortDirection;
  view: LibraryView;
  groupBy: TimelineGrouping;
  statuses: AssetQueryStatus[];
}

const assetSorts = new Set<AssetSort>([
  'capturedAt',
  'importedAt',
  'updatedAt',
  'title',
]);
const directions = new Set<SortDirection>(['asc', 'desc']);
const views = new Set<LibraryView>(['grid', 'list']);
const groupings = new Set<TimelineGrouping>(['day', 'month', 'year']);
const statuses = new Set<AssetQueryStatus>(['processing', 'ready', 'failed']);

export const defaultLibraryState: LibraryState = {
  search: '',
  sort: 'capturedAt',
  direction: 'desc',
  view: 'grid',
  groupBy: 'day',
  statuses: [],
};

function allowed<T extends string>(
  value: string | null,
  values: ReadonlySet<T>,
  fallback: T,
) {
  return value && values.has(value as T) ? (value as T) : fallback;
}

export function parseLibraryState(params: URLSearchParams): LibraryState {
  const selectedStatuses = (params.get('status') ?? '')
    .split(',')
    .filter((status): status is AssetQueryStatus =>
      statuses.has(status as AssetQueryStatus),
    );

  return {
    search: (params.get('q') ?? '').trim(),
    sort: allowed(params.get('sort'), assetSorts, defaultLibraryState.sort),
    direction: allowed(
      params.get('direction'),
      directions,
      defaultLibraryState.direction,
    ),
    view: allowed(params.get('view'), views, defaultLibraryState.view),
    groupBy: allowed(
      params.get('group'),
      groupings,
      defaultLibraryState.groupBy,
    ),
    statuses: [...new Set(selectedStatuses)],
  };
}

export function libraryStateToSearchParams(state: LibraryState) {
  const params = new URLSearchParams();

  if (state.search) params.set('q', state.search);
  if (state.sort !== defaultLibraryState.sort) params.set('sort', state.sort);
  if (state.direction !== defaultLibraryState.direction) {
    params.set('direction', state.direction);
  }
  if (state.view !== defaultLibraryState.view) params.set('view', state.view);
  if (state.groupBy !== defaultLibraryState.groupBy) {
    params.set('group', state.groupBy);
  }
  if (state.statuses.length > 0) {
    params.set('status', state.statuses.join(','));
  }

  return params;
}
