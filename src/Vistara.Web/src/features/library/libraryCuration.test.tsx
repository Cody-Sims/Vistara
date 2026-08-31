import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { AssetSummary, TimelinePage } from '../../api/generated';
import { LibraryPage, type LibraryDataSource } from './LibraryPage';

function asset(index: number): AssetSummary {
  return {
    id: `asset-${index}`,
    title: `Photo ${index}`,
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
        kind: 'grid',
        path: `/media/${index}.webp`,
        width: 1024,
        height: 768,
        contentType: 'image/webp',
      },
    ],
    version: 2,
  };
}

function page(items: readonly AssetSummary[]): TimelinePage {
  return {
    groups: [
      {
        key: '2026-06-10',
        label: 'June 10, 2026',
        startsAt: '2026-06-10T00:00:00Z',
        endsAt: '2026-06-11T00:00:00Z',
        items,
      },
    ],
  };
}

function curationClient(overrides: Record<string, unknown> = {}) {
  return {
    getAsset: vi.fn(),
    favoriteAsset: vi.fn(async () => ({
      data: {
        asset: { ...asset(0), favorite: true, version: 3 },
        metadata: { restrictedMetadataAvailable: false },
        albums: [],
      },
    })),
    unfavoriteAsset: vi.fn(),
    addAssetTag: vi.fn(),
    removeAssetTag: vi.fn(),
    listTags: vi.fn(async () => ({ data: { items: [] } })),
    createTag: vi.fn(),
    listAlbums: vi.fn(async () => ({ data: { items: [] } })),
    getAlbum: vi.fn(),
    createAlbum: vi.fn(),
    updateAlbum: vi.fn(),
    addAlbumItems: vi.fn(),
    removeAlbumItems: vi.fn(),
    bulkMutateAssets: vi.fn(async () => ({
      data: [{ assetId: 'asset-0', status: 'trashed', version: 3 }],
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

type TimelineReader = LibraryDataSource['getTimeline'];

function renderLibrary(options: {
  client?: ReturnType<typeof curationClient>;
  getTimeline?: ReturnType<typeof vi.fn<TimelineReader>>;
  canCurate?: boolean;
}) {
  const client = options.client ?? curationClient();
  const getTimeline =
    options.getTimeline ??
    vi.fn<TimelineReader>(async () => ({ data: page([asset(0), asset(1)]) }));
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/library']}>
        <LibraryPage
          curation={{
            client: client as never,
            canCurate: options.canCurate ?? true,
          }}
          dataSource={{ getTimeline }}
          layout={{ columns: 4, viewportHeight: 700 }}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return { client, getTimeline, user: userEvent.setup() };
}

describe('library curation', () => {
  it('offers no curation actions until something is selected', async () => {
    renderLibrary({});

    expect(
      await screen.findByRole('heading', { name: 'June 10, 2026' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Curation actions' })).toBeNull();
  });

  it('curates the selected assets and reloads the timeline afterwards', async () => {
    const { client, getTimeline, user } = renderLibrary({});

    await screen.findByRole('heading', { name: 'June 10, 2026' });
    await user.click(screen.getByLabelText('Select Photo 0'));

    const actions = await screen.findByRole('group', {
      name: 'Curation actions',
    });
    expect(actions).toHaveTextContent('1 image selected');
    const reads = getTimeline.mock.calls.length;
    await user.click(within(actions).getByRole('button', { name: 'Favorite' }));

    await waitFor(() =>
      expect(client.favoriteAsset).toHaveBeenCalledWith('asset-0', {
        idempotencyKey: expect.any(String),
        ifMatch: '"v2"',
      }),
    );
    await waitFor(() =>
      expect(getTimeline.mock.calls.length).toBeGreaterThan(reads),
    );
  });

  it('drops trashed assets from the selection and reloads', async () => {
    const { client, user } = renderLibrary({});

    await screen.findByRole('heading', { name: 'June 10, 2026' });
    await user.click(screen.getByLabelText('Select Photo 0'));
    const actions = await screen.findByRole('group', {
      name: 'Curation actions',
    });
    await user.click(
      within(actions).getByRole('button', { name: 'Move to trash' }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.click(
      within(dialog).getByRole('button', { name: 'Move to trash' }),
    );

    await waitFor(() => expect(client.bulkMutateAssets).toHaveBeenCalled());
    await waitFor(() =>
      expect(screen.getByLabelText('Select Photo 0')).not.toBeChecked(),
    );

    // What happened, and the undo, outlive the assets that left the timeline.
    const report = screen.getByRole('group', { name: 'Curation actions' });
    expect(
      within(report).getByRole('status', { name: 'Curation result' }),
    ).toHaveTextContent('Moved to trash: 1 image.');
    expect(
      within(report).getByRole('button', { name: 'Undo move to trash' }),
    ).toBeInTheDocument();
    expect(
      within(report).queryByRole('button', { name: 'Move to trash' }),
    ).toBeNull();

    await user.click(screen.getByLabelText('Select Photo 1'));
    expect(
      within(
        screen.getByRole('group', { name: 'Curation actions' }),
      ).queryByRole('button', { name: 'Undo move to trash' }),
    ).toBeNull();
  });

  it('keeps the restore result on screen after the undo', async () => {
    const { client, user } = renderLibrary({});

    await screen.findByRole('heading', { name: 'June 10, 2026' });
    await user.click(screen.getByLabelText('Select Photo 0'));
    await user.click(
      screen.getByRole('button', { name: 'Move to trash' }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.click(
      within(dialog).getByRole('button', { name: 'Move to trash' }),
    );
    const undo = await screen.findByRole('button', {
      name: 'Undo move to trash',
    });
    await user.click(undo);

    await waitFor(() => expect(client.restoreAssets).toHaveBeenCalled());
    expect(
      await screen.findByRole('status', { name: 'Curation result' }),
    ).toHaveTextContent('Restore queued for 1 image.');
  });

  it('hides the actions from a session that may not curate', async () => {
    const { user } = renderLibrary({ canCurate: false });

    await screen.findByRole('heading', { name: 'June 10, 2026' });
    await user.click(screen.getByLabelText('Select Photo 0'));

    expect(screen.queryByRole('group', { name: 'Curation actions' })).toBeNull();
  });
});
