import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type {
  AlbumDetail,
  AlbumSummary,
  AssetSummary,
} from '../../api/generated/models';
import { AlbumDetailView, AlbumsView } from './AlbumsView';

const album: AlbumSummary = {
  id: 'album-1',
  name: 'Summer',
  itemCount: 0,
  updatedAt: '2026-08-29T00:00:00Z',
  version: 3,
};

const asset: AssetSummary = {
  id: 'asset-1',
  title: 'Boardwalk',
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
  favorite: false,
  tags: [],
  renditions: [],
  version: 5,
};

function detail(
  overrides: Partial<AlbumDetail['album']> = {},
): AlbumDetail {
  return {
    album: { ...album, itemCount: 1, ...overrides },
    items: {
      items: [
        {
          asset,
          position: 100,
          addedAt: '2026-08-29T00:00:00Z',
        },
      ],
    },
  };
}

describe('curation albums', () => {
  it('shows loading and empty states, then creates an album', async () => {
    const user = userEvent.setup();
    let finishList: ((value: { data: { items: AlbumSummary[] } }) => void) | undefined;
    const client = {
      listAlbums: vi.fn(
        () =>
          new Promise<{ data: { items: AlbumSummary[] } }>((resolve) => {
            finishList = resolve;
          }),
      ),
      createAlbum: vi.fn().mockResolvedValue({
        data: { album, items: { items: [] } },
        etag: '"v3"',
      }),
    };

    render(<AlbumsView client={client} />);

    expect(screen.getByRole('status')).toHaveTextContent('Loading albums');
    finishList?.({ data: { items: [] } });
    expect(
      await screen.findByText('No albums yet. Create one to organize your gallery.'),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText('Album name'), 'Summer');
    await user.click(screen.getByRole('button', { name: 'Create album' }));

    expect(client.createAlbum).toHaveBeenCalledWith(
      { name: 'Summer' },
      expect.objectContaining({ idempotencyKey: expect.any(String) }),
    );
    expect(
      await screen.findByRole('link', { name: /Summer/ }),
    ).toBeInTheDocument();
  });

  it('optimistically renames and restores the server version after an ETag conflict', async () => {
    const user = userEvent.setup();
    let rejectRename: ((reason: unknown) => void) | undefined;
    const client = {
      getAlbum: vi
        .fn()
        .mockResolvedValueOnce({ data: detail() })
        .mockResolvedValueOnce({
          data: detail({ name: 'Summer (team edit)', version: 4 }),
          etag: '"v4"',
        }),
      updateAlbum: vi.fn(
        () =>
          new Promise<never>((_resolve, reject) => {
            rejectRename = reject;
          }),
      ),
      deleteAlbum: vi.fn(),
      reorderAlbumItems: vi.fn(),
      removeAlbumItems: vi.fn(),
    };

    render(<AlbumDetailView albumId="album-1" client={client} />);
    await screen.findByRole('heading', { name: 'Summer' });

    const name = screen.getByLabelText('Album name');
    await user.clear(name);
    await user.type(name, 'Beach days');
    await user.click(screen.getByRole('button', { name: 'Save album name' }));

    expect(screen.getByRole('heading', { name: 'Beach days' })).toBeInTheDocument();
    expect(client.updateAlbum).toHaveBeenCalledWith(
      'album-1',
      { name: 'Beach days' },
      expect.objectContaining({ ifMatch: '"v3"' }),
    );

    rejectRename?.(conflict());

    expect(
      await screen.findByRole('heading', { name: 'Summer (team edit)' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(
      'This album changed elsewhere. The latest version was restored.',
    );
  });

  it('offers button ordering and reports each removed item', async () => {
    const user = userEvent.setup();
    const secondAsset = { ...asset, id: 'asset-2', title: 'Pier', version: 8 };
    const initial: AlbumDetail = {
      album: { ...album, itemCount: 2 },
      items: {
        items: [
          { asset, position: 100, addedAt: '2026-08-29T00:00:00Z' },
          {
            asset: secondAsset,
            position: 200,
            addedAt: '2026-08-29T00:00:00Z',
          },
        ],
      },
    };
    const reordered: AlbumDetail = {
      ...initial,
      album: { ...initial.album, version: 4 },
      items: {
        items: [
          initial.items.items[1]!,
          initial.items.items[0]!,
        ],
      },
    };
    const client = {
      getAlbum: vi.fn().mockResolvedValue({ data: initial }),
      updateAlbum: vi.fn(),
      deleteAlbum: vi.fn(),
      reorderAlbumItems: vi.fn().mockResolvedValue({
        data: reordered,
        etag: '"v4"',
      }),
      removeAlbumItems: vi.fn().mockResolvedValue({
        data: {
          album: { ...album, itemCount: 1, version: 5 },
          items: { items: [reordered.items.items[1]!] },
        },
        etag: '"v5"',
      }),
    };

    render(<AlbumDetailView albumId="album-1" client={client} />);
    await screen.findByRole('heading', { name: 'Summer' });

    await user.click(screen.getByRole('button', { name: 'Move Pier up' }));
    expect(client.reorderAlbumItems).toHaveBeenCalledWith(
      'album-1',
      {
        items: [
          { assetId: 'asset-2', position: 100 },
          { assetId: 'asset-1', position: 200 },
        ],
      },
      expect.objectContaining({ ifMatch: '"v3"' }),
    );

    await user.click(screen.getByRole('checkbox', { name: 'Select Pier' }));
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    expect(await screen.findByText('Pier: Removed')).toBeInTheDocument();
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
