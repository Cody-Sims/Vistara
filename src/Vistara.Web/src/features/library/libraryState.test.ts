import { describe, expect, it } from 'vitest';
import {
  libraryStateToSearchParams,
  parseLibraryState,
} from './libraryState';

describe('library URL state', () => {
  it('normalizes searchable, sortable, and presentation state from the URL', () => {
    const state = parseLibraryState(
      new URLSearchParams(
        'q=  northern lights  &sort=title&direction=asc&view=list&group=month&status=ready,processing',
      ),
    );

    expect(state).toEqual({
      search: 'northern lights',
      sort: 'title',
      direction: 'asc',
      view: 'list',
      groupBy: 'month',
      statuses: ['ready', 'processing'],
    });
  });

  it('writes only non-default values so library URLs stay stable and shareable', () => {
    const params = libraryStateToSearchParams({
      search: 'portraits',
      sort: 'capturedAt',
      direction: 'desc',
      view: 'grid',
      groupBy: 'day',
      statuses: [],
    });

    expect(params.toString()).toBe('q=portraits');
  });
});
