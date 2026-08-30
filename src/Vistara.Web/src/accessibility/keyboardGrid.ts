import {
  useCallback,
  useMemo,
  useRef,
  useState,
  type HTMLAttributes,
  type KeyboardEvent,
} from 'react';

export type GridDirection = 'ltr' | 'rtl';

export interface GridCell {
  rowIndex: number;
  columnIndex: number;
}

export interface GridKeyboardInput {
  key: string;
  ctrlKey?: boolean;
  metaKey?: boolean;
}

export interface GridDimensions {
  rowCount: number;
  columnCount: number;
  direction: GridDirection;
}

export interface GridRowRange {
  startRowIndex: number;
  endRowIndex: number;
}

function clamp(value: number, maximum: number): number {
  return Math.min(Math.max(value, 0), maximum);
}

export function getPreservedGridRows(
  visibleRows: GridRowRange,
  activeRowIndex: number,
  rowCount: number,
): GridRowRange {
  const lastRow = Math.max(rowCount - 1, 0);
  const activeRow = clamp(activeRowIndex, lastRow);
  return {
    startRowIndex: Math.min(
      clamp(visibleRows.startRowIndex, lastRow),
      activeRow,
    ),
    endRowIndex: Math.max(
      clamp(visibleRows.endRowIndex, lastRow),
      activeRow,
    ),
  };
}

export function getGridNavigationTarget(
  current: GridCell,
  input: GridKeyboardInput,
  dimensions: GridDimensions,
): GridCell | null {
  const lastRow = dimensions.rowCount - 1;
  const lastColumn = dimensions.columnCount - 1;
  let { rowIndex, columnIndex } = current;
  const startOrEndModifier = input.ctrlKey || input.metaKey;

  switch (input.key) {
    case 'ArrowLeft':
      columnIndex += dimensions.direction === 'rtl' ? 1 : -1;
      break;
    case 'ArrowRight':
      columnIndex += dimensions.direction === 'rtl' ? -1 : 1;
      break;
    case 'ArrowUp':
      rowIndex -= 1;
      break;
    case 'ArrowDown':
      rowIndex += 1;
      break;
    case 'Home':
      rowIndex = startOrEndModifier ? 0 : rowIndex;
      columnIndex = 0;
      break;
    case 'End':
      rowIndex = startOrEndModifier ? lastRow : rowIndex;
      columnIndex = lastColumn;
      break;
    case 'PageUp':
      rowIndex = 0;
      break;
    case 'PageDown':
      rowIndex = lastRow;
      break;
    default:
      return null;
  }

  return {
    rowIndex: clamp(rowIndex, lastRow),
    columnIndex: clamp(columnIndex, lastColumn),
  };
}

export interface KeyboardGridOptions {
  rowCount: number;
  columnCount: number;
  direction?: GridDirection;
  initialCell?: GridCell;
  onActiveCellChange?: (cell: GridCell) => void;
}

type CellProps = HTMLAttributes<HTMLElement> & {
  ref: (element: HTMLElement | null) => void;
  role: 'gridcell';
  tabIndex: number;
  'aria-rowindex': number;
  'aria-colindex': number;
};

export function useKeyboardGrid({
  rowCount,
  columnCount,
  direction = 'ltr',
  initialCell = { rowIndex: 0, columnIndex: 0 },
  onActiveCellChange,
}: KeyboardGridOptions) {
  if (rowCount < 1 || columnCount < 1) {
    throw new Error('Keyboard grids require at least one row and one column.');
  }

  const [activeCell, setActiveCell] = useState<GridCell>({
    rowIndex: clamp(initialCell.rowIndex, rowCount - 1),
    columnIndex: clamp(initialCell.columnIndex, columnCount - 1),
  });
  const cells = useRef(new Map<string, HTMLElement>());

  const activate = useCallback(
    (cell: GridCell, focus: boolean) => {
      setActiveCell(cell);
      onActiveCellChange?.(cell);
      if (focus) {
        cells.current.get(`${cell.rowIndex}:${cell.columnIndex}`)?.focus();
      }
    },
    [onActiveCellChange],
  );

  const getCellProps = useCallback(
    (rowIndex: number, columnIndex: number): CellProps => {
      const cell = { rowIndex, columnIndex };
      const key = `${rowIndex}:${columnIndex}`;
      const isActive =
        activeCell.rowIndex === rowIndex &&
        activeCell.columnIndex === columnIndex;

      return {
        ref: (element) => {
          if (element) {
            cells.current.set(key, element);
          } else {
            cells.current.delete(key);
          }
        },
        role: 'gridcell',
        tabIndex: isActive ? 0 : -1,
        'aria-rowindex': rowIndex + 1,
        'aria-colindex': columnIndex + 1,
        onFocus: () => activate(cell, false),
        onKeyDown: (event: KeyboardEvent<HTMLElement>) => {
          const target = getGridNavigationTarget(
            cell,
            {
              key: event.key,
              ctrlKey: event.ctrlKey,
              metaKey: event.metaKey,
            },
            { rowCount, columnCount, direction },
          );
          if (!target) {
            return;
          }

          event.preventDefault();
          activate(target, true);
        },
      };
    },
    [
      activeCell.columnIndex,
      activeCell.rowIndex,
      activate,
      columnCount,
      direction,
      rowCount,
    ],
  );

  const gridProps = useMemo(
    () =>
      ({
        role: 'grid',
        dir: direction,
        'aria-rowcount': rowCount,
        'aria-colcount': columnCount,
      }) as const,
    [columnCount, direction, rowCount],
  );

  const getRowProps = useCallback(
    (rowIndex: number) =>
      ({
        role: 'row',
        'aria-rowindex': rowIndex + 1,
      }) as const,
    [],
  );

  return {
    activeCell,
    gridProps,
    getRowProps,
    getCellProps,
  };
}
