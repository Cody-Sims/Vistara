import { describe, expect, it } from 'vitest';
import {
  defaultSearchState,
  describeFilters,
  isEmptySearch,
  parseSearchState,
  searchStateToSearchParams,
  toAssetListQuery,
} from './searchState';

describe('search state', () => {
  it('reads a full query from the address', () => {
    const state = parseSearchState(
      new URLSearchParams(
        'q=%20harbour%20&sort=title&direction=asc&status=ready,failed&favorite=true&from=2026-01-01&to=2026-02-01',
      ),
    );

    expect(state).toEqual({
      query: 'harbour',
      sort: 'title',
      direction: 'asc',
      statuses: ['ready', 'failed'],
      favorite: true,
      capturedFrom: '2026-01-01',
      capturedTo: '2026-02-01',
    });
  });

  it('drops unsupported sorts, statuses, and dates', () => {
    const state = parseSearchState(
      new URLSearchParams(
        'sort=random&direction=sideways&status=purged,ready,ready&from=yesterday&to=2026-13-45',
      ),
    );

    expect(state).toEqual({
      ...defaultSearchState,
      statuses: ['ready'],
    });
  });

  it('round-trips to a stable address that omits defaults', () => {
    const params = searchStateToSearchParams({
      ...defaultSearchState,
      query: 'harbour lights',
      favorite: true,
      statuses: ['ready'],
    });

    expect(params.toString()).toBe(
      'q=harbour+lights&status=ready&favorite=true',
    );
    expect(parseSearchState(params)).toEqual({
      ...defaultSearchState,
      query: 'harbour lights',
      favorite: true,
      statuses: ['ready'],
    });
  });

  it('recognises a search without any criteria', () => {
    expect(isEmptySearch(defaultSearchState)).toBe(true);
    expect(
      isEmptySearch({ ...defaultSearchState, favorite: true }),
    ).toBe(false);
  });

  it('summarises the active criteria for review', () => {
    expect(
      describeFilters({
        ...defaultSearchState,
        query: 'harbour',
        favorite: true,
        statuses: ['failed'],
        capturedFrom: '2026-01-01',
      }),
    ).toEqual([
      '“harbour”',
      'Favorites only',
      'Status: failed',
      'Captured from 2026-01-01',
    ]);
  });

  it('builds a bounded API query with whole-day capture ranges', () => {
    expect(
      toAssetListQuery(
        {
          ...defaultSearchState,
          query: 'harbour',
          statuses: ['ready'],
          favorite: true,
          capturedFrom: '2026-01-01',
          capturedTo: '2026-02-01',
        },
        'cursor-2',
      ),
    ).toEqual({
      limit: 60,
      search: 'harbour',
      statuses: ['ready'],
      favorite: true,
      capturedFrom: '2026-01-01T00:00:00Z',
      capturedTo: '2026-02-01T23:59:59Z',
      sort: 'capturedAt',
      direction: 'desc',
      cursor: 'cursor-2',
    });
  });
});
