import { describe, expect, it } from 'vitest';
import {
  createSelectionState,
  selectAllResults,
  selectRange,
  toggleSelection,
} from './selection';

const visible = ['a', 'b', 'c', 'd', 'e'];

describe('library selection', () => {
  it('extends a keyboard range from the last selected asset', () => {
    const initial = toggleSelection(createSelectionState(), 'b');

    const range = selectRange(initial, visible, 'e');

    expect(range.mode).toBe('explicit');
    if (range.mode === 'explicit') {
      expect(range.selectedIds).toEqual(new Set(['b', 'c', 'd', 'e']));
    }
  });

  it('supports selecting every result without materializing every id', () => {
    const selection = selectAllResults(createSelectionState(), 8_500);

    expect(selection).toMatchObject({
      mode: 'all',
      totalCount: 8_500,
      excludedIds: new Set(),
    });
    const excluded = toggleSelection(selection, 'asset-2');
    expect(excluded.mode).toBe('all');
    if (excluded.mode === 'all') {
      expect(excluded.excludedIds).toEqual(new Set(['asset-2']));
    }
  });
});
