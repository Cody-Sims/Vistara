import { describe, expect, it } from 'vitest';
import {
  captureFocusRestorer,
  getViewerReturnAddress,
} from './viewerState';

describe('viewer restoration state', () => {
  it('preserves exact internal library state and rejects unrelated return paths', () => {
    expect(
      getViewerReturnAddress({
        libraryAddress: '/library?q=moon&view=list',
      }),
    ).toBe('/library?q=moon&view=list');
    expect(
      getViewerReturnAddress({ libraryAddress: 'https://example.com/library' }),
    ).toBe('/library');
    expect(getViewerReturnAddress({ libraryAddress: '/settings' })).toBe(
      '/library',
    );
  });

  it('restores focus to a still-mounted opener', () => {
    const opener = document.createElement('button');
    const other = document.createElement('button');
    document.body.append(opener, other);
    opener.focus();
    const restore = captureFocusRestorer();

    other.focus();
    restore();

    expect(opener).toHaveFocus();
    opener.remove();
    other.remove();
  });
});
