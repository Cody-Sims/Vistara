import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import {
  getPreservedGridRows,
  getGridNavigationTarget,
  useKeyboardGrid,
  type GridDirection,
} from './keyboardGrid';

function GridHarness({ direction = 'ltr' }: { direction?: GridDirection }) {
  const grid = useKeyboardGrid({
    rowCount: 2,
    columnCount: 3,
    direction,
  });

  return (
    <div {...grid.gridProps} aria-label="Media">
      {[0, 1].map((rowIndex) => (
        <div key={rowIndex} {...grid.getRowProps(rowIndex)}>
          {[0, 1, 2].map((columnIndex) => (
            <button
              key={columnIndex}
              {...grid.getCellProps(rowIndex, columnIndex)}
            >
              {rowIndex + 1},{columnIndex + 1}
            </button>
          ))}
        </div>
      ))}
    </div>
  );
}

describe('keyboard grid foundations', () => {
  it('calculates the complete bounded grid keyboard model', () => {
    expect(
      getGridNavigationTarget(
        { rowIndex: 1, columnIndex: 1 },
        { key: 'Home', ctrlKey: true },
        { rowCount: 3, columnCount: 4, direction: 'ltr' },
      ),
    ).toEqual({ rowIndex: 0, columnIndex: 0 });
    expect(
      getGridNavigationTarget(
        { rowIndex: 1, columnIndex: 1 },
        { key: 'End', ctrlKey: true },
        { rowCount: 3, columnCount: 4, direction: 'ltr' },
      ),
    ).toEqual({ rowIndex: 2, columnIndex: 3 });
    expect(
      getGridNavigationTarget(
        { rowIndex: 1, columnIndex: 1 },
        { key: 'PageUp' },
        { rowCount: 3, columnCount: 4, direction: 'ltr' },
      ),
    ).toEqual({ rowIndex: 0, columnIndex: 1 });
  });

  it('keeps an active virtualized row inside the mounted range', () => {
    expect(
      getPreservedGridRows(
        { startRowIndex: 10, endRowIndex: 20 },
        4,
        100,
      ),
    ).toEqual({ startRowIndex: 4, endRowIndex: 20 });
    expect(
      getPreservedGridRows(
        { startRowIndex: 10, endRowIndex: 20 },
        27,
        100,
      ),
    ).toEqual({ startRowIndex: 10, endRowIndex: 27 });
  });

  it('provides roving focus and semantic row and cell metadata', async () => {
    const user = userEvent.setup();
    render(<GridHarness />);

    const grid = screen.getByRole('grid', { name: 'Media' });
    const cells = screen.getAllByRole('gridcell');
    expect(grid).toHaveAttribute('aria-rowcount', '2');
    expect(grid).toHaveAttribute('aria-colcount', '3');
    expect(screen.getAllByRole('row')).toHaveLength(2);
    expect(cells.filter((cell) => cell.tabIndex === 0)).toHaveLength(1);

    cells[0]?.focus();
    await user.keyboard('{ArrowRight}{ArrowDown}{Home}');
    expect(screen.getByRole('gridcell', { name: '2,1' })).toHaveFocus();

    await user.keyboard('{Control>}{End}{/Control}');
    expect(screen.getByRole('gridcell', { name: '2,3' })).toHaveFocus();
  });

  it('reverses horizontal arrow behavior for right-to-left grids', async () => {
    const user = userEvent.setup();
    render(<GridHarness direction="rtl" />);

    screen.getByRole('gridcell', { name: '1,2' }).focus();
    await user.keyboard('{ArrowLeft}');

    expect(screen.getByRole('gridcell', { name: '1,3' })).toHaveFocus();
  });
});
