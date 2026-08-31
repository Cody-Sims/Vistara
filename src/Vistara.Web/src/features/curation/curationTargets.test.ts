import { describe, expect, it } from 'vitest';
import type { AssetSummary } from '../../api/generated';
import {
  albumStateFor,
  curationTargets,
  favoriteStateFor,
  tagStateFor,
  toVersionedReferences,
  trashableTargets,
} from './curationTargets';

function asset(overrides: Partial<AssetSummary> = {}): AssetSummary {
  return {
    id: 'asset-1',
    title: 'Mountain',
    status: 'ready',
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/png',
    format: 'png',
    width: 100,
    height: 80,
    sizeBytes: 2048,
    importedAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    favorite: false,
    tags: [],
    renditions: [],
    version: 3,
    ...overrides,
  };
}

describe('curation targets', () => {
  it('carries the version the API needs for each selected asset', () => {
    const references = toVersionedReferences([
      asset({ id: 'a', version: 3 }),
      asset({ id: 'b', version: 9 }),
    ]);

    expect(references).toEqual([
      { id: 'a', version: 3 },
      { id: 'b', version: 9 },
    ]);
  });

  it('reports a favorite state only when every asset agrees', () => {
    expect(favoriteStateFor([asset({ favorite: true })])).toBe('all');
    expect(favoriteStateFor([asset({ favorite: false })])).toBe('none');
    expect(
      favoriteStateFor([
        asset({ id: 'a', favorite: true }),
        asset({ id: 'b', favorite: false }),
      ]),
    ).toBe('some');
    expect(favoriteStateFor([])).toBe('none');
  });

  it('reports a tag state across the selection', () => {
    const tagged = asset({
      id: 'a',
      tags: [{ id: 'tag-1', name: 'Coast' }],
    });
    const untagged = asset({ id: 'b' });

    expect(tagStateFor([tagged], 'tag-1')).toBe('all');
    expect(tagStateFor([tagged, untagged], 'tag-1')).toBe('some');
    expect(tagStateFor([untagged], 'tag-1')).toBe('none');
  });

  it('reports album membership from the albums each asset is known to be in', () => {
    expect(albumStateFor([{ id: 'a', albumIds: ['album-1'] }], 'album-1')).toBe(
      'all',
    );
    expect(
      albumStateFor(
        [
          { id: 'a', albumIds: ['album-1'] },
          { id: 'b', albumIds: [] },
        ],
        'album-1',
      ),
    ).toBe('some');
    expect(albumStateFor([{ id: 'b', albumIds: [] }], 'album-1')).toBe('none');
    expect(albumStateFor([{ id: 'b' }], 'album-1')).toBe('unknown');
  });

  it('only offers the trash for assets the API can move there', () => {
    const ready = asset({ id: 'a' });
    const processing = asset({ id: 'b', status: 'processing' });
    const trashed = asset({ id: 'c', status: 'trashed' });

    expect(trashableTargets([ready, processing, trashed])).toEqual([ready]);
  });

  it('keeps a stable, de-duplicated target list', () => {
    const first = asset({ id: 'a' });
    const second = asset({ id: 'b' });

    expect(curationTargets([first, second, first])).toEqual([first, second]);
  });
});
