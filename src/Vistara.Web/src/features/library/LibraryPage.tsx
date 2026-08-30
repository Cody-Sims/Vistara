import { useInfiniteQuery } from '@tanstack/react-query';
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type FormEvent,
  type KeyboardEvent,
  type MouseEvent,
} from 'react';
import { Link, useLocation, useSearchParams } from 'react-router-dom';
import type {
  ApiResponse,
  AssetStatus,
  TimelineGroup,
  TimelinePage,
  TimelineQuery,
} from '../../api/generated';
import { Skeleton } from '../../components';
import { buildResponsiveImage } from '../viewer/responsiveImage';
import {
  defaultLibraryState,
  libraryStateToSearchParams,
  parseLibraryState,
  type LibraryState,
} from './libraryState';
import {
  createLibraryRestorationStore,
  type LibraryRestorationState,
} from './libraryRestoration';
import {
  createSelectionState,
  isSelected,
  selectAllResults,
  selectRange,
  selectVisible,
  selectionCount,
  toggleSelection,
} from './selection';
import {
  buildTimelineRows,
  virtualizeTimelineRows,
} from './virtualTimeline';
import styles from './LibraryPage.module.css';

export interface LibraryDataSource {
  getTimeline(query: TimelineQuery): Promise<ApiResponse<TimelinePage>>;
}

export interface LibraryLayout {
  columns: number;
  viewportHeight: number;
}

interface LibraryPageProps {
  dataSource: LibraryDataSource;
  layout?: LibraryLayout;
  restorationStore?: ReturnType<typeof createLibraryRestorationStore>;
}

const statuses: ReadonlyArray<{ value: AssetStatus; label: string }> = [
  { value: 'ready', label: 'Ready' },
  { value: 'processing', label: 'Processing' },
  { value: 'failed', label: 'Failed' },
];
const assetDateFormatter = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
});
const compactNumberFormatter = new Intl.NumberFormat(undefined, {
  notation: 'compact',
  maximumFractionDigits: 1,
});

function layoutForViewport(): LibraryLayout {
  const width = typeof window === 'undefined' ? 1280 : window.innerWidth;
  return {
    columns: width < 480 ? 2 : width < 768 ? 3 : width < 1100 ? 4 : 5,
    viewportHeight:
      typeof window === 'undefined'
        ? 700
        : Math.max(400, Math.min(850, window.innerHeight - 190)),
  };
}

function useResponsiveLayout(override?: LibraryLayout) {
  const [layout, setLayout] = useState(layoutForViewport);

  useEffect(() => {
    if (override) return;
    const measure = () => setLayout(layoutForViewport());
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, [override]);

  return override ?? layout;
}

function toTimelineQuery(state: LibraryState): TimelineQuery {
  return {
    limit: 100,
    search: state.search || undefined,
    statuses: state.statuses.length > 0 ? state.statuses : undefined,
    sort: state.sort,
    direction: state.direction,
    groupBy: state.groupBy,
  };
}

function mergeTimelinePages(pages: readonly TimelinePage[]): TimelinePage {
  const groups = new Map<
    string,
    TimelineGroup & { items: AssetSummaryMutable[] }
  >();

  for (const page of pages) {
    for (const group of page.groups) {
      const existing = groups.get(group.key);
      if (existing) {
        existing.items.push(...group.items);
      } else {
        groups.set(group.key, {
          ...group,
          items: [...group.items],
        });
      }
    }
  }

  return { groups: [...groups.values()] };
}

type AssetSummaryMutable = TimelineGroup['items'][number];

function useRestoration(
  address: string,
  focusedAssetId: string | undefined,
  setFocusedAssetId: (assetId: string | undefined) => void,
  setScrollTop: (scrollTop: number) => void,
  enabled: boolean,
  overrideStore?: ReturnType<typeof createLibraryRestorationStore>,
) {
  const scrollerRef = useRef<HTMLDivElement>(null);
  const defaultStore = useMemo(() => {
    if (typeof sessionStorage === 'undefined') return undefined;
    return createLibraryRestorationStore(sessionStorage);
  }, []);
  const store = overrideStore ?? defaultStore;
  const saved = useRef<LibraryRestorationState | null>(null);
  const focusedAssetIdRef = useRef(focusedAssetId);

  useEffect(() => {
    focusedAssetIdRef.current = focusedAssetId;
  }, [focusedAssetId]);

  useEffect(() => {
    if (!enabled) return;
    saved.current = store?.read(address) ?? null;
    const scroller = scrollerRef.current;
    if (scroller && saved.current) {
      scroller.scrollTop = saved.current.scrollTop;
      setScrollTop(saved.current.scrollTop);
    }
    setFocusedAssetId(saved.current?.focusedAssetId);

    return () => {
      if (!scroller) return;
      store?.save(address, {
        scrollTop: scroller.scrollTop,
        focusedAssetId: focusedAssetIdRef.current,
      });
    };
  }, [address, enabled, setFocusedAssetId, setScrollTop, store]);

  useEffect(() => {
    if (
      !enabled ||
      !saved.current?.focusedAssetId ||
      focusedAssetId !== saved.current.focusedAssetId
    ) {
      return;
    }
    scrollerRef.current
      ?.querySelector<HTMLElement>(
        `[data-asset-link="${saved.current.focusedAssetId.replaceAll('"', '\\"')}"]`,
      )
      ?.focus({ preventScroll: true });
  }, [enabled, focusedAssetId]);

  return scrollerRef;
}

function updateStatus(
  state: LibraryState,
  status: AssetStatus,
  checked: boolean,
) {
  return {
    ...state,
    statuses: checked
      ? [...state.statuses, status]
      : state.statuses.filter((value) => value !== status),
  };
}

export function LibraryPage({
  dataSource,
  layout: layoutOverride,
  restorationStore,
}: LibraryPageProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  const location = useLocation();
  const state = useMemo(() => parseLibraryState(searchParams), [searchParams]);
  const [selection, setSelection] = useState(createSelectionState);
  const [focusedAssetId, setFocusedAssetId] = useState<string>();
  const [scrollTop, setScrollTop] = useState(0);
  const layout = useResponsiveLayout(layoutOverride);
  const address = `${location.pathname}${location.search}`;

  const query = useInfiniteQuery({
    queryKey: ['library-timeline', state],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      (
        await dataSource.getTimeline({
          ...toTimelineQuery(state),
          cursor: pageParam,
        })
      ).data,
    getNextPageParam: (lastPage) => lastPage.nextCursor,
  });
  const page = useMemo(
    () =>
      query.data ? mergeTimelinePages(query.data.pages) : undefined,
    [query.data],
  );
  const rows = useMemo(
    () => buildTimelineRows(page?.groups ?? [], state.view, layout.columns),
    [layout.columns, page?.groups, state.view],
  );
  const virtual = useMemo(
    () =>
      virtualizeTimelineRows(rows, {
        scrollTop,
        viewportHeight: layout.viewportHeight,
        overscan: 2,
        focusedAssetId,
      }),
    [focusedAssetId, layout.viewportHeight, rows, scrollTop],
  );
  const allAssets = useMemo(
    () => page?.groups.flatMap((group) => group.items) ?? [],
    [page?.groups],
  );
  const viewportRows = virtual.rows.filter(
    ({ offset, size }) =>
      offset + size > scrollTop &&
      offset < scrollTop + layout.viewportHeight,
  );
  const visibleIds = viewportRows.flatMap(({ row }) =>
    row.type === 'assets' ? row.assets.map((asset) => asset.id) : [],
  );
  const priorityAssetId = viewportRows
    .flatMap(({ row }) => (row.type === 'assets' ? row.assets : []))
    .find((asset) => asset.status === 'ready')?.id;
  const orderedIds = allAssets.map((asset) => asset.id);
  const selectedCount = selectionCount(selection);
  const defaultStore = useRestoration(
    address,
    focusedAssetId,
    setFocusedAssetId,
    setScrollTop,
    Boolean(page),
    restorationStore,
  );
  const activeScrollerRef = defaultStore;

  function setState(next: LibraryState) {
    setSearchParams(libraryStateToSearchParams(next));
    setSelection(createSelectionState());
  }

  function submitSearch(event: FormEvent) {
    event.preventDefault();
    const value = new FormData(event.currentTarget as HTMLFormElement).get(
      'search',
    );
    setState({ ...state, search: typeof value === 'string' ? value.trim() : '' });
  }

  function handleAssetKeyDown(
    event: KeyboardEvent<HTMLAnchorElement>,
    assetId: string,
  ) {
    const current = orderedIds.indexOf(assetId);
    const delta =
      event.key === 'ArrowRight'
        ? 1
        : event.key === 'ArrowLeft'
          ? -1
          : event.key === 'ArrowDown'
            ? state.view === 'grid'
              ? layout.columns
              : 1
            : event.key === 'ArrowUp'
              ? state.view === 'grid'
                ? -layout.columns
                : -1
              : 0;
    if (delta === 0) return;
    const nextId = orderedIds[current + delta];
    if (!nextId) return;
    event.preventDefault();
    activeScrollerRef.current
      ?.querySelector<HTMLElement>(
        `[data-asset-link="${nextId.replaceAll('"', '\\"')}"]`,
      )
      ?.focus();
  }

  return (
    <section className={styles.library} aria-labelledby="library-heading">
      <header className={styles.heading}>
        <div>
          <p className={styles.eyebrow}>Your image control plane</p>
          <h1 id="library-heading">Library</h1>
        </div>
        <div className={styles.viewSwitch} aria-label="Library view">
          {(['grid', 'list'] as const).map((view) => (
            <button
              aria-label={`${view === 'grid' ? 'Grid' : 'List'} view`}
              aria-pressed={state.view === view}
              className={styles.controlButton}
              key={view}
              onClick={() => setState({ ...state, view })}
              type="button"
            >
              {view === 'grid' ? 'Grid' : 'List'}
            </button>
          ))}
        </div>
      </header>

      <form className={styles.filters} onSubmit={submitSearch} role="search">
        <label className={styles.searchLabel}>
          <span>Search library</span>
          <input
            defaultValue={state.search}
            key={state.search}
            name="search"
            type="search"
          />
        </label>
        <button className={styles.primaryButton} type="submit">
          Apply search
        </button>
        <label>
          <span>Sort library</span>
          <select
            onChange={(event) =>
              setState({
                ...state,
                sort: event.currentTarget.value as LibraryState['sort'],
              })
            }
            value={state.sort}
          >
            <option value="capturedAt">Capture date</option>
            <option value="importedAt">Import date</option>
            <option value="updatedAt">Updated date</option>
            <option value="title">Title</option>
          </select>
        </label>
        <label>
          <span>Sort direction</span>
          <select
            onChange={(event) =>
              setState({
                ...state,
                direction: event.currentTarget
                  .value as LibraryState['direction'],
              })
            }
            value={state.direction}
          >
            <option value="desc">Descending</option>
            <option value="asc">Ascending</option>
          </select>
        </label>
        <label>
          <span>Timeline grouping</span>
          <select
            onChange={(event) =>
              setState({
                ...state,
                groupBy: event.currentTarget
                  .value as LibraryState['groupBy'],
              })
            }
            value={state.groupBy}
          >
            <option value="day">Day</option>
            <option value="month">Month</option>
            <option value="year">Year</option>
          </select>
        </label>
        <fieldset className={styles.statuses}>
          <legend>Status</legend>
          {statuses.map(({ value, label }) => (
            <label key={value}>
              <input
                checked={state.statuses.includes(value)}
                onChange={(event) =>
                  setState(updateStatus(state, value, event.currentTarget.checked))
                }
                type="checkbox"
              />
              <span>{label}</span>
            </label>
          ))}
        </fieldset>
      </form>

      {query.isPending ? (
        <div className={styles.statePanel} aria-busy="true">
          <p role="status" aria-live="polite">
            Loading library…
          </p>
          <Skeleton count={12} shape="tile" />
        </div>
      ) : null}

      {query.isError && !page ? (
        <div className={styles.statePanel} role="alert">
          <strong>Could not load the library.</strong>
          <span> Check your connection and try again.</span>
          <button
            className={styles.controlButton}
            onClick={() => void query.refetch()}
            type="button"
          >
            Retry
          </button>
        </div>
      ) : null}

      {page && allAssets.length === 0 ? (
        <div className={styles.statePanel}>
          <h2>No images found</h2>
          <p>
            {state.search || state.statuses.length > 0
              ? 'Try adjusting your search or filters.'
              : 'Images will appear here after they are imported.'}
          </p>
          {state.search || state.statuses.length > 0 ? (
            <button
              className={styles.controlButton}
              onClick={() => setState(defaultLibraryState)}
              type="button"
            >
              Clear filters
            </button>
          ) : null}
        </div>
      ) : null}

      {page && allAssets.length > 0 ? (
        <>
          <div className={styles.selectionBar}>
            <button
              className={styles.controlButton}
              onClick={() => setSelection(selectVisible(selection, visibleIds))}
              type="button"
            >
              Select visible
            </button>
            <button
              className={styles.controlButton}
              onClick={() =>
                setSelection(selectAllResults(selection, allAssets.length))
              }
              type="button"
            >
              {query.hasNextPage ? 'Select loaded results' : 'Select all results'}
            </button>
            {selectedCount > 0 ? (
              <>
                <span
                  aria-label="Selection status"
                  aria-live="polite"
                  role="status"
                >
                  {selectedCount.toLocaleString()} selected
                </span>
                <button
                  className={styles.controlButton}
                  onClick={() => setSelection(createSelectionState())}
                  type="button"
                >
                  Clear selection
                </button>
              </>
            ) : null}
          </div>

          <div
            aria-label="Library timeline"
            className={styles.scroller}
            onScroll={(event) => {
              const scroller = event.currentTarget;
              setScrollTop(scroller.scrollTop);
              if (
                query.hasNextPage &&
                !query.isFetchingNextPage &&
                scroller.scrollHeight -
                  scroller.scrollTop -
                  scroller.clientHeight <
                  600
              ) {
                void query.fetchNextPage();
              }
            }}
            ref={activeScrollerRef}
            role="region"
            style={{ '--viewport-height': `${layout.viewportHeight}px` } as CSSProperties}
            tabIndex={-1}
          >
            <ol
              className={styles.timeline}
              style={{ blockSize: `${virtual.totalHeight}px` }}
            >
              {virtual.rows.map(({ offset, row }) => {
                if (row.type === 'heading') {
                  return (
                    <li
                      className={styles.virtualRow}
                      key={row.key}
                      style={{ transform: `translateY(${offset}px)` }}
                    >
                      <h2 className={styles.dateHeading}>{row.label}</h2>
                    </li>
                  );
                }

                return (
                  <li
                    className={styles.virtualRow}
                    key={row.key}
                    style={{ transform: `translateY(${offset}px)` }}
                  >
                    <ul
                      className={
                        state.view === 'grid' ? styles.assetGrid : styles.assetList
                      }
                      style={
                        {
                          '--columns': layout.columns,
                        } as CSSProperties
                      }
                    >
                      {row.assets.map((asset) => {
                        const responsive = buildResponsiveImage(
                          asset,
                          'grid',
                          asset.id === priorityAssetId,
                        );
                        const selected = isSelected(selection, asset.id);

                        return (
                          <li
                            className={`${styles.assetItem} ${
                              selected ? styles.selected : ''
                            }`}
                            data-asset-id={asset.id}
                            key={asset.id}
                          >
                            <input
                              aria-label={`Select ${asset.title}`}
                              checked={selected}
                              className={styles.selectControl}
                              onClick={(event: MouseEvent<HTMLInputElement>) =>
                                setSelection(
                                  event.shiftKey
                                    ? selectRange(selection, orderedIds, asset.id)
                                    : toggleSelection(selection, asset.id),
                                )
                              }
                              readOnly
                              type="checkbox"
                            />
                            <Link
                              className={styles.assetLink}
                              data-asset-link={asset.id}
                              onFocus={() => setFocusedAssetId(asset.id)}
                              onKeyDown={(event) =>
                                handleAssetKeyDown(event, asset.id)
                              }
                              state={{ libraryAddress: address }}
                              to={`/assets/${encodeURIComponent(asset.id)}`}
                            >
                              <span className={styles.preview}>
                                {responsive ? (
                                  <img {...responsive} />
                                ) : (
                                  <span className={styles.placeholder}>
                                    {asset.status === 'processing'
                                      ? 'Processing preview'
                                      : asset.status === 'failed'
                                        ? 'Preview failed'
                                        : 'Preview unavailable'}
                                  </span>
                                )}
                              </span>
                              <span className={styles.assetText}>
                                <strong>{asset.title}</strong>
                                <span>
                                  {asset.capturedAt
                                    ? assetDateFormatter.format(
                                        new Date(asset.capturedAt),
                                      )
                                    : 'Imported date'}
                                </span>
                              </span>
                              {state.view === 'list' ? (
                                <span className={styles.listMetadata}>
                                  <span>
                                    {asset.width.toLocaleString()} ×{' '}
                                    {asset.height.toLocaleString()}
                                  </span>
                                  <span>{asset.format.toUpperCase()}</span>
                                  <span>
                                    {compactNumberFormatter.format(
                                      asset.sizeBytes,
                                    )}{' '}
                                    bytes
                                  </span>
                                </span>
                              ) : null}
                            </Link>
                          </li>
                        );
                      })}
                    </ul>
                  </li>
                );
              })}
            </ol>
          </div>
          {query.hasNextPage ? (
            <button
              className={styles.loadMore}
              disabled={query.isFetchingNextPage}
              onClick={() => void query.fetchNextPage()}
              type="button"
            >
              {query.isFetchingNextPage ? 'Loading more images…' : 'Load more images'}
            </button>
          ) : null}
          {query.isFetchNextPageError ? (
            <div className={styles.statePanel} role="alert">
              <span>Could not load more images.</span>
              <button
                className={styles.controlButton}
                onClick={() => void query.fetchNextPage()}
                type="button"
              >
                Retry loading more
              </button>
            </div>
          ) : null}
        </>
      ) : null}
    </section>
  );
}
