import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { AssetDetail, AssetSummary } from '../../api/generated';
import { ViewerPage } from './ViewerPage';

function asset(overrides: Partial<AssetSummary> = {}): AssetSummary {
  return {
    id: 'asset-1',
    title: 'Mountain',
    status: 'ready',
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/jpeg',
    format: 'jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 42_000,
    importedAt: '2026-06-11T12:00:00Z',
    updatedAt: '2026-06-11T12:00:00Z',
    favorite: false,
    tags: [],
    renditions: [
      {
        kind: 'viewer',
        path: '/media/viewer.webp',
        width: 1600,
        height: 1200,
        contentType: 'image/webp',
      },
    ],
    version: 5,
    ...overrides,
  };
}

function detail(overrides: Partial<AssetSummary> = {}): AssetDetail {
  return {
    asset: asset(overrides),
    metadata: { restrictedMetadataAvailable: false },
    albums: [{ id: 'album-1', name: 'Summer' }],
  };
}

function curationClient(overrides: Record<string, unknown> = {}) {
  return {
    getAsset: vi.fn(async () => ({ data: detail() })),
    favoriteAsset: vi.fn(async () => ({
      data: detail({ favorite: true, version: 6 }),
    })),
    unfavoriteAsset: vi.fn(),
    addAssetTag: vi.fn(),
    removeAssetTag: vi.fn(),
    listTags: vi.fn(async () => ({ data: { items: [] } })),
    createTag: vi.fn(),
    listAlbums: vi.fn(async () => ({
      data: {
        items: [
          {
            id: 'album-1',
            name: 'Summer',
            itemCount: 1,
            updatedAt: '2026-06-11T12:00:00Z',
            version: 1,
          },
        ],
      },
    })),
    getAlbum: vi.fn(),
    createAlbum: vi.fn(),
    updateAlbum: vi.fn(),
    addAlbumItems: vi.fn(),
    removeAlbumItems: vi.fn(),
    bulkMutateAssets: vi.fn(async () => ({
      data: [{ assetId: 'asset-1', status: 'trashed', version: 6 }],
    })),
    restoreAssets: vi.fn(async () => ({
      data: {
        jobId: 'job-1',
        state: 'queued',
        submittedCount: 1,
        submittedAt: '2026-06-11T12:00:00Z',
      },
    })),
    ...overrides,
  };
}

function renderViewer(client = curationClient()) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const router = createMemoryRouter(
    [
      {
        path: '/assets/:assetId',
        element: (
          <ViewerPage
            curation={{ client: client as never, canCurate: true }}
            dataSource={client as never}
          />
        ),
      },
      { path: '/library', element: <h1>Library</h1> },
    ],
    { initialEntries: ['/assets/asset-1'] },
  );

  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );

  return { client, router, user: userEvent.setup() };
}

describe('viewer curation', () => {
  it('curates the asset on screen with the version it was read at', async () => {
    const { client, user } = renderViewer();

    await screen.findByRole('heading', { level: 1, name: 'Mountain' });
    const actions = screen.getByRole('group', { name: 'Curation actions' });
    await user.click(within(actions).getByRole('button', { name: 'Favorite' }));

    await waitFor(() =>
      expect(client.favoriteAsset).toHaveBeenCalledWith('asset-1', {
        idempotencyKey: expect.any(String),
        ifMatch: '"v5"',
      }),
    );
  });

  it('knows which albums the asset is already in', async () => {
    const { user } = renderViewer();

    await screen.findByRole('heading', { level: 1, name: 'Mountain' });
    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });

    expect(
      within(panel).getByRole('button', { name: 'Add to Summer' }),
    ).toBeDisabled();
    expect(
      within(panel).getByRole('button', { name: 'Remove from Summer' }),
    ).toBeEnabled();
  });

  it('replaces the asset with a trashed confirmation that can be undone', async () => {
    const { client, router, user } = renderViewer();

    await screen.findByRole('heading', { level: 1, name: 'Mountain' });
    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(
      within(dialog).getByRole('button', { name: 'Move to trash' }),
    );

    const heading = await screen.findByRole('heading', {
      level: 1,
      name: 'Moved to trash',
    });
    expect(heading).toHaveFocus();
    expect(client.bulkMutateAssets).toHaveBeenCalled();
    expect(
      screen.getByRole('link', { name: 'Back to library' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Undo move to trash' }));

    await waitFor(() => expect(client.restoreAssets).toHaveBeenCalled());
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Mountain' }),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/assets/asset-1');
  });
});
