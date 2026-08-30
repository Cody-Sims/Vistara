import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { AssetSummary } from '../../api/generated/models';
import { FavoritesView } from './FavoritesView';

function asset(id: string, title: string, version: number): AssetSummary {
  return {
    id,
    title,
    status: 'ready',
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/jpeg',
    format: 'jpeg',
    width: 1200,
    height: 800,
    sizeBytes: 42,
    importedAt: '2026-08-29T00:00:00Z',
    updatedAt: '2026-08-29T00:00:00Z',
    favorite: true,
    tags: [],
    renditions: [],
    version,
  };
}

const boardwalk = asset('asset-1', 'Boardwalk', 4);
const pier = asset('asset-2', 'Pier', 7);

describe('curation favorites', () => {
  it('loads every result for select all and reports bulk outcomes per item', async () => {
    const user = userEvent.setup();
    const client = {
      listAssets: vi
        .fn()
        .mockResolvedValueOnce({
          data: { items: [boardwalk], nextCursor: 'page-2' },
        })
        .mockResolvedValueOnce({ data: { items: [pier] } }),
      unfavoriteAsset: vi
        .fn()
        .mockResolvedValueOnce({
          data: { asset: { ...boardwalk, favorite: false }, metadata: {}, albums: [] },
          etag: '"v5"',
        })
        .mockRejectedValueOnce(conflict()),
      favoriteAsset: vi.fn(),
      getAsset: vi.fn().mockResolvedValue({
        data: { asset: { ...pier, version: 8 }, metadata: {}, albums: [] },
        etag: '"v8"',
      }),
    };

    render(<FavoritesView client={client} />);
    await screen.findByText('Boardwalk');

    await user.click(screen.getByRole('button', { name: 'Select all results' }));
    expect(client.listAssets).toHaveBeenLastCalledWith({
      favorite: true,
      cursor: 'page-2',
    });
    expect(await screen.findByText('2 selected')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Remove favorites' }));

    expect(await screen.findByText('Boardwalk: Removed')).toBeInTheDocument();
    expect(
      await screen.findByText('Pier: Conflict — kept favorite'),
    ).toBeInTheDocument();
    expect(client.unfavoriteAsset).toHaveBeenNthCalledWith(
      1,
      'asset-1',
      expect.objectContaining({ ifMatch: '"v4"' }),
    );
  });

  it('restores a favorite when a single optimistic update conflicts', async () => {
    const user = userEvent.setup();
    let rejectUpdate: ((reason: unknown) => void) | undefined;
    const client = {
      listAssets: vi.fn().mockResolvedValue({ data: { items: [boardwalk] } }),
      unfavoriteAsset: vi.fn(
        () =>
          new Promise<never>((_resolve, reject) => {
            rejectUpdate = reject;
          }),
      ),
      favoriteAsset: vi.fn(),
      getAsset: vi.fn().mockResolvedValue({
        data: { asset: { ...boardwalk, version: 5 }, metadata: {}, albums: [] },
        etag: '"v5"',
      }),
    };

    render(<FavoritesView client={client} />);
    await screen.findByText('Boardwalk');
    await user.click(
      screen.getByRole('button', { name: 'Remove Boardwalk from favorites' }),
    );

    expect(screen.queryByText('Boardwalk')).not.toBeInTheDocument();
    rejectUpdate?.(conflict());
    expect(await screen.findByText('Boardwalk')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Boardwalk changed elsewhere. The latest version was restored.',
    );
  });

  it('renders useful empty and error recovery states', async () => {
    const user = userEvent.setup();
    const client = {
      listAssets: vi
        .fn()
        .mockRejectedValueOnce(new Error('offline'))
        .mockResolvedValueOnce({ data: { items: [] } }),
      unfavoriteAsset: vi.fn(),
      favoriteAsset: vi.fn(),
      getAsset: vi.fn(),
    };

    render(<FavoritesView client={client} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Favorites could not be loaded.',
    );
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(
      await screen.findByText('No favorites yet. Favorite an image to find it here.'),
    ).toBeInTheDocument();
  });
});

function conflict() {
  return new VistaraApiError(412, {
    type: 'about:blank',
    title: 'Precondition failed',
    status: 412,
    code: 'version_conflict',
    errors: {},
  });
}
