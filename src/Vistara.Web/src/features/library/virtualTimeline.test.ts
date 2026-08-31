import { describe, expect, it } from 'vitest';
import type { AssetSummary, TimelineGroup } from '../../api/generated';
import {
  buildTimelineRows,
  pageTimelineRows,
  virtualizeTimelineRows,
} from './virtualTimeline';

function asset(index: number): AssetSummary {
  return {
    id: `asset-${index}`,
    title: `Asset ${index}`,
    status: 'ready',
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/jpeg',
    format: 'jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 1000,
    importedAt: '2026-01-01T12:00:00Z',
    updatedAt: '2026-01-01T12:00:00Z',
    favorite: false,
    tags: [],
    renditions: [],
    version: 1,
  };
}

const group: TimelineGroup = {
  key: '2026-01',
  label: 'January 2026',
  startsAt: '2026-01-01T00:00:00Z',
  endsAt: '2026-02-01T00:00:00Z',
  items: Array.from({ length: 800 }, (_, index) => asset(index)),
};

describe('timeline virtualization', () => {
  it('computes grouped grid rows while keeping thumbnail DOM far below budget', () => {
    const rows = buildTimelineRows([group], 'grid', 4);
    const window = virtualizeTimelineRows(rows, {
      scrollTop: 4_000,
      viewportHeight: 700,
      overscan: 2,
    });

    expect(rows[0]).toMatchObject({ type: 'heading', label: 'January 2026' });
    expect(window.totalHeight).toBeGreaterThan(40_000);
    expect(
      window.rows.flatMap((row) =>
        row.row.type === 'assets' ? row.row.assets : [],
      ),
    ).toHaveLength(28);
  });

  it('keeps the focused row mounted even after it scrolls outside overscan', () => {
    const rows = buildTimelineRows([group], 'list', 1);
    const window = virtualizeTimelineRows(rows, {
      scrollTop: 0,
      viewportHeight: 400,
      overscan: 1,
      focusedAssetId: 'asset-700',
    });

    expect(
      window.rows.some(
        ({ row }) =>
          row.type === 'assets' &&
          row.assets.some((item) => item.id === 'asset-700'),
      ),
    ).toBe(true);
  });
});

describe('paged timeline', () => {
  const rows = Array.from({ length: 7 }, (_, index) => ({
    type: 'heading' as const,
    key: `row-${index}`,
    label: `Row ${index}`,
    size: 10 + index,
  }));

  it('lays a page out from the top with no gaps', () => {
    const paged = pageTimelineRows(rows, 2, 3);

    expect(paged.page).toBe(2);
    expect(paged.pageCount).toBe(3);
    expect(paged.rows.map((entry) => entry.row.key)).toEqual([
      'row-3',
      'row-4',
      'row-5',
    ]);
    expect(paged.rows.map((entry) => entry.offset)).toEqual([0, 13, 27]);
    expect(paged.totalHeight).toBe(13 + 14 + 15);
  });

  it('clamps a page beyond the end and keeps at least one page', () => {
    expect(pageTimelineRows(rows, 99, 3).page).toBe(3);
    expect(pageTimelineRows(rows, 0, 3).page).toBe(1);
    expect(pageTimelineRows([], 1, 3)).toMatchObject({
      page: 1,
      pageCount: 1,
      totalHeight: 0,
    });
  });
});
