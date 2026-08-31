import type { AssetSummary, TimelineGroup } from '../../api/generated';
import type { LibraryView } from './libraryState';

export type TimelineRow =
  | {
      type: 'heading';
      key: string;
      label: string;
      size: number;
    }
  | {
      type: 'assets';
      key: string;
      assets: readonly AssetSummary[];
      size: number;
    };

export interface VirtualTimelineRow {
  index: number;
  offset: number;
  size: number;
  row: TimelineRow;
}

interface VirtualTimelineOptions {
  scrollTop: number;
  viewportHeight: number;
  overscan: number;
  focusedAssetId?: string;
}

interface TimelineMetrics {
  offsets: readonly number[];
  totalHeight: number;
  averageSize: number;
  assetRows: ReadonlyMap<string, number>;
}

const metricsCache = new WeakMap<readonly TimelineRow[], TimelineMetrics>();

function getTimelineMetrics(rows: readonly TimelineRow[]): TimelineMetrics {
  const cached = metricsCache.get(rows);
  if (cached) return cached;

  const offsets: number[] = [];
  const assetRows = new Map<string, number>();
  let totalHeight = 0;

  rows.forEach((row, index) => {
    offsets.push(totalHeight);
    totalHeight += row.size;
    if (row.type === 'assets') {
      row.assets.forEach((asset) => assetRows.set(asset.id, index));
    }
  });

  const metrics = {
    offsets,
    totalHeight,
    averageSize: rows.length === 0 ? 0 : totalHeight / rows.length,
    assetRows,
  };
  metricsCache.set(rows, metrics);
  return metrics;
}

function findFirstVisibleRow(
  rows: readonly TimelineRow[],
  offsets: readonly number[],
  start: number,
) {
  let low = 0;
  let high = rows.length;

  while (low < high) {
    const middle = Math.floor((low + high) / 2);
    const row = rows[middle];
    if ((offsets[middle] ?? 0) + (row?.size ?? 0) <= start) {
      low = middle + 1;
    } else {
      high = middle;
    }
  }

  return low;
}

export function buildTimelineRows(
  groups: readonly TimelineGroup[],
  view: LibraryView,
  requestedColumns: number,
) {
  const columns = view === 'list' ? 1 : Math.max(1, Math.min(8, requestedColumns));
  const assetRowSize = view === 'list' ? 76 : 260;
  const rows: TimelineRow[] = [];

  for (const group of groups) {
    rows.push({
      type: 'heading',
      key: `heading:${group.key}`,
      label: group.label,
      size: 52,
    });

    for (let index = 0; index < group.items.length; index += columns) {
      rows.push({
        type: 'assets',
        key: `${group.key}:${index}`,
        assets: group.items.slice(index, index + columns),
        size: assetRowSize,
      });
    }
  }

  return rows;
}

export function virtualizeTimelineRows(
  rows: readonly TimelineRow[],
  options: VirtualTimelineOptions,
) {
  const metrics = getTimelineMetrics(rows);
  const overscanPixels = Math.max(0, options.overscan) * metrics.averageSize;
  const windowStart = Math.max(0, options.scrollTop - overscanPixels);
  const windowEnd =
    options.scrollTop + Math.max(0, options.viewportHeight) + overscanPixels;
  const included = new Set<number>();
  let index = findFirstVisibleRow(rows, metrics.offsets, windowStart);

  while (index < rows.length && (metrics.offsets[index] ?? 0) < windowEnd) {
    included.add(index);
    index += 1;
  }

  if (options.focusedAssetId) {
    const focusedRow = metrics.assetRows.get(options.focusedAssetId);
    if (focusedRow !== undefined) included.add(focusedRow);
  }

  return {
    totalHeight: metrics.totalHeight,
    rows: [...included]
      .sort((left, right) => left - right)
      .map(
        (index): VirtualTimelineRow => ({
          index,
          offset: metrics.offsets[index] ?? 0,
          size: rows[index]?.size ?? 0,
          row: rows[index] as TimelineRow,
        }),
      ),
  };
}

export interface PagedTimeline {
  readonly rows: readonly VirtualTimelineRow[];
  readonly totalHeight: number;
  readonly page: number;
  readonly pageCount: number;
}

/**
 * Lays out one page of rows for readers who chose paged mode. Every row on the
 * page stays mounted, so a screen reader or keyboard user can reach the end of
 * the page and move on deliberately instead of scrolling forever.
 */
export function pageTimelineRows(
  rows: readonly TimelineRow[],
  page: number,
  rowsPerPage: number,
): PagedTimeline {
  const size = Math.max(1, Math.trunc(rowsPerPage));
  const pageCount = Math.max(1, Math.ceil(rows.length / size));
  const current = Math.min(Math.max(1, Math.trunc(page)), pageCount);
  const start = (current - 1) * size;
  const slice = rows.slice(start, start + size);

  let offset = 0;
  const laid = slice.map((row, index) => {
    const placed = {
      index: start + index,
      offset,
      size: row.size,
      row,
    };
    offset += row.size;
    return placed;
  });

  return {
    rows: laid,
    totalHeight: offset,
    page: current,
    pageCount,
  };
}
