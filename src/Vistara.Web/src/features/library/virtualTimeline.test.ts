import { describe, expect, it } from 'vitest';
import type { AssetSummary, TimelineGroup } from '../../api/generated';
import {
  buildTimelineRows,
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
