import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import type {
  ApiResponse,
  AssetListQuery,
  AssetSort,
  AssetStatus,
  AssetSummary,
  CursorPage,
  SortDirection,
} from '../../api/generated';
import { useAppPreferences } from '../../app/preferences';
import { Skeleton } from '../../components';
import { buildResponsiveImage } from '../viewer/responsiveImage';
import {
  defaultSearchState,
  describeFilters,
  isEmptySearch,
  parseSearchState,
  searchStateToSearchParams,
  toAssetListQuery,
  type SearchState,
} from './searchState';
import styles from './search.module.css';

export interface SearchClient {
  listAssets(
    query: AssetListQuery,
  ): Promise<ApiResponse<CursorPage<AssetSummary>>>;
}

interface SearchViewProps {
  readonly client: SearchClient;
}

type ResultState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'searching' }
  | { readonly kind: 'extending' }
  | { readonly kind: 'failed' }
  | { readonly kind: 'ready' };

const statusChoices: readonly { value: AssetStatus; label: string }[] = [
  { value: 'ready', label: 'Ready' },
  { value: 'processing', label: 'Processing' },
  { value: 'failed', label: 'Failed' },
];

const sortChoices: readonly { value: AssetSort; label: string }[] = [
  { value: 'capturedAt', label: 'Capture date' },
  { value: 'importedAt', label: 'Import date' },
  { value: 'updatedAt', label: 'Last updated' },
  { value: 'title', label: 'Title' },
];

export function SearchView({ client }: SearchViewProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  const applied = parseSearchState(searchParams);
  const address = searchStateToSearchParams(applied).toString();

  const [draft, setDraft] = useState<SearchState>(applied);
  const [draftAddress, setDraftAddress] = useState(address);
  const [items, setItems] = useState<readonly AssetSummary[]>([]);
  const [cursor, setCursor] = useState<string>();
  const [pageCursors, setPageCursors] = useState<readonly string[]>([]);
  const [pageIndex, setPageIndex] = useState(0);
  const [state, setState] = useState<ResultState>(() =>
    isEmptySearch(applied) ? { kind: 'idle' } : { kind: 'searching' },
  );
  const [attempt, setAttempt] = useState(0);
  const request = useRef(0);
  const resultsHeading = useRef<HTMLHeadingElement>(null);
  const paged = useAppPreferences().screenReaderPagedMode;
  const empty = isEmptySearch(applied);
  const pageCursor = pageIndex === 0 ? undefined : pageCursors[pageIndex - 1];

  if (draftAddress !== address) {
    setDraftAddress(address);
    setDraft(applied);
    setItems([]);
    setCursor(undefined);
    setPageCursors([]);
    setPageIndex(0);
    setState(empty ? { kind: 'idle' } : { kind: 'searching' });
  }

  useEffect(() => {
    if (empty) {
      return;
    }

    const id = ++request.current;
    const query = toAssetListQuery(
      parseSearchState(new URLSearchParams(address)),
      paged ? pageCursor : undefined,
    );

    void client.listAssets(query).then(
      (response) => {
        if (request.current !== id) {
          return;
        }

        setItems(response.data.items);
        setCursor(response.data.nextCursor);
        setState({ kind: 'ready' });
      },
      () => {
        if (request.current === id) {
          setState({ kind: 'failed' });
        }
      },
    );

    return () => {
      request.current += 1;
    };
  }, [address, attempt, client, empty, pageCursor, paged]);

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSearchParams(searchStateToSearchParams(draft), { replace: false });
    resultsHeading.current?.focus();
  }

  function extend() {
    if (!cursor || paged) {
      return;
    }

    const id = ++request.current;
    setState({ kind: 'extending' });
    void client.listAssets(toAssetListQuery(applied, cursor)).then(
      (response) => {
        if (request.current !== id) {
          return;
        }

        setItems((current) => [...current, ...response.data.items]);
        setCursor(response.data.nextCursor);
        setState({ kind: 'ready' });
      },
      () => {
        if (request.current === id) {
          setState({ kind: 'failed' });
        }
      },
    );
  }

  const filters = describeFilters(applied);

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <p className={styles.eyebrow}>Find an image</p>
        <h1>Search</h1>
        <p className={styles.description}>
          Search titles, descriptions, and tags, then narrow the results by
          status, favorites, or capture date. Every search stays in the address
          bar so it can be shared or reopened.
        </p>
      </header>

      <form className={styles.form} onSubmit={submit} role="search">
        <div className={styles.queryRow}>
          <label className={styles.queryLabel} htmlFor="search-query">
            Search your library
          </label>
          <input
            autoComplete="off"
            className={styles.queryInput}
            id="search-query"
            name="q"
            type="search"
            value={draft.query}
            onChange={(event) =>
              setDraft((current) => ({ ...current, query: event.target.value }))
            }
          />
          <button className={styles.submit} type="submit">
            Search
          </button>
        </div>

        <div className={styles.filters}>
          <fieldset className={styles.filterGroup}>
            <legend>Status</legend>
            {statusChoices.map((choice) => (
              <label className={styles.checkbox} key={choice.value}>
                <input
                  checked={draft.statuses.includes(choice.value)}
                  type="checkbox"
                  onChange={(event) =>
                    setDraft((current) => ({
                      ...current,
                      statuses: event.target.checked
                        ? [...current.statuses, choice.value]
                        : current.statuses.filter(
                            (status) => status !== choice.value,
                          ),
                    }))
                  }
                />
                {choice.label}
              </label>
            ))}
            <label className={styles.checkbox}>
              <input
                aria-label="Favorites only"
                checked={draft.favorite}
                type="checkbox"
                onChange={(event) =>
                  setDraft((current) => ({
                    ...current,
                    favorite: event.target.checked,
                  }))
                }
              />
              Favorites only
            </label>
          </fieldset>

          <div className={styles.filterGroup}>
            <label htmlFor="search-from">Captured from</label>
            <input
              className={styles.control}
              id="search-from"
              type="date"
              value={draft.capturedFrom}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  capturedFrom: event.target.value,
                }))
              }
            />
            <label htmlFor="search-to">Captured to</label>
            <input
              className={styles.control}
              id="search-to"
              type="date"
              value={draft.capturedTo}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  capturedTo: event.target.value,
                }))
              }
            />
          </div>

          <div className={styles.filterGroup}>
            <label htmlFor="search-sort">Sort by</label>
            <select
              className={styles.control}
              id="search-sort"
              value={draft.sort}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  sort: event.target.value as AssetSort,
                }))
              }
            >
              {sortChoices.map((choice) => (
                <option key={choice.value} value={choice.value}>
                  {choice.label}
                </option>
              ))}
            </select>
            <label htmlFor="search-direction">Order</label>
            <select
              className={styles.control}
              id="search-direction"
              value={draft.direction}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  direction: event.target.value as SortDirection,
                }))
              }
            >
              <option value="desc">Newest first</option>
              <option value="asc">Oldest first</option>
            </select>
          </div>
        </div>

        {filters.length > 0 ? (
          <div className={styles.appliedFilters}>
            <span className={styles.appliedLabel}>Applied</span>
            <ul className={styles.chips} aria-label="Applied filters">
              {filters.map((filter) => (
                <li className={styles.chip} key={filter}>
                  {filter}
                </li>
              ))}
            </ul>
            <button
              className={styles.clear}
              type="button"
              onClick={() => {
                setDraft(defaultSearchState);
                setSearchParams(new URLSearchParams());
              }}
            >
              Clear filters
            </button>
          </div>
        ) : null}
      </form>

      <section aria-labelledby="search-results-heading">
        <h2
          className={styles.resultsHeading}
          id="search-results-heading"
          ref={resultsHeading}
          tabIndex={-1}
        >
          Results
        </h2>
        <p className={styles.resultStatus} role="status" aria-live="polite">
          {state.kind === 'searching'
            ? 'Searching your library…'
            : state.kind === 'ready' || state.kind === 'extending'
              ? paged
                ? `Page ${pageIndex + 1} · ${items.length} results on this page`
                : `${items.length}${cursor ? '+' : ''} results`
              : ''}
        </p>

        {state.kind === 'idle' ? (
          <div className={styles.placeholder}>
            <p>
              Start with a word from a title, a description, or a tag. Filters
              work on their own too.
            </p>
          </div>
        ) : null}

        {state.kind === 'searching' ? (
          <Skeleton count={8} shape="tile" />
        ) : null}

        {state.kind === 'failed' ? (
          <div className={styles.failure}>
            <h3>Search failed</h3>
            <p>
              The library could not be searched. Your filters are unchanged, so
              you can try the same search again.
            </p>
            <button
              className={styles.submit}
              type="button"
              onClick={() => {
                setState({ kind: 'searching' });
                setAttempt((value) => value + 1);
              }}
            >
              Try again
            </button>
          </div>
        ) : null}

        {(state.kind === 'ready' || state.kind === 'extending') &&
        items.length === 0 ? (
          <div className={styles.placeholder}>
            <h3>No images match</h3>
            <p>
              Try fewer filters, a shorter word, or a wider capture range.
            </p>
            <button
              className={styles.clear}
              type="button"
              onClick={() => {
                setDraft(defaultSearchState);
                setSearchParams(new URLSearchParams());
              }}
            >
              Reset search
            </button>
          </div>
        ) : null}

        {items.length > 0 ? (
          <ul className={styles.results} aria-label="Search results">
            {items.map((item, index) => {
              const image = buildResponsiveImage(item, 'grid', index === 0);

              return (
                <li className={styles.result} key={item.id}>
                  <Link className={styles.resultLink} to={`/assets/${item.id}`}>
                    <span className={styles.thumbnail}>
                      {image ? (
                        <img {...image} alt="" />
                      ) : (
                        <span className={styles.thumbnailFallback} aria-hidden="true">
                          {item.status === 'processing' ? '⏳' : '🖼'}
                        </span>
                      )}
                    </span>
                    <span className={styles.resultTitle}>{item.title}</span>
                    <span className={styles.resultMeta}>
                      {formatDate(item.capturedAt ?? item.importedAt)} ·{' '}
                      {item.width}×{item.height}
                    </span>
                  </Link>
                </li>
              );
            })}
          </ul>
        ) : null}

        {paged && !empty ? (
          <nav className={styles.pager} aria-label="Result pages">
            <button
              className={styles.clear}
              disabled={pageIndex === 0 || state.kind === 'searching'}
              type="button"
              onClick={() => setPageIndex((value) => Math.max(0, value - 1))}
            >
              Previous page
            </button>
            <button
              className={styles.more}
              disabled={!cursor || state.kind === 'searching'}
              type="button"
              onClick={() => {
                if (!cursor) {
                  return;
                }

                setPageCursors((current) => {
                  const next = current.slice(0, pageIndex);
                  next.push(cursor);
                  return next;
                });
                setPageIndex((value) => value + 1);
                setState({ kind: 'searching' });
              }}
            >
              Next page
            </button>
          </nav>
        ) : null}

        {!paged && cursor ? (
          <button
            className={styles.more}
            disabled={state.kind === 'extending'}
            type="button"
            onClick={extend}
          >
            {state.kind === 'extending' ? 'Loading…' : 'Show more results'}
          </button>
        ) : null}
      </section>
    </div>
  );
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? 'Unknown date'
    : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(date);
}
