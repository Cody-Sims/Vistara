import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { AssetSummary } from '../../api/generated/models';
import { SessionContext } from '../session/sessionContext';
import type { SessionContextValue } from '../session/sessionContext';
import { CurationActions } from './CurationActions';

function session(
  scopes: SessionContextValue['scopes'],
): SessionContextValue {
  return {
    status: 'authenticated',
    credentialKind: 'cookie',
    scopes,
    canAdminister: false,
    signOutIncomplete: false,
    signIn: () => Promise.reject(new Error('not used')),
    signOut: () => Promise.resolve(),
    reload: () => Promise.resolve(),
  };
}

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

function detail(value: AssetSummary) {
  return {
    data: {
      asset: value,
      metadata: { restrictedMetadataAvailable: false },
      albums: [],
    },
  };
}

function apiError(status: number) {
  return new VistaraApiError(status, {
    type: 'about:blank',
    title: 'conflict',
    status,
    code: 'conflict',
    errors: {},
  });
}

const tag = { id: 'tag-1', name: 'Coast', assetCount: 2, version: 1 };
const album = {
  id: 'album-1',
  name: 'Summer',
  itemCount: 0,
  updatedAt: '2026-01-01T00:00:00Z',
  version: 2,
};

function makeClient(overrides: Record<string, unknown> = {}) {
  return {
    getAsset: vi.fn(async () => detail(asset())),
    favoriteAsset: vi.fn(async () => detail(asset({ favorite: true }))),
    unfavoriteAsset: vi.fn(async () => detail(asset({ favorite: false }))),
    addAssetTag: vi.fn(async () =>
      detail(asset({ tags: [{ id: tag.id, name: tag.name }] })),
    ),
    removeAssetTag: vi.fn(async () => detail(asset())),
    listTags: vi.fn(async () => ({ data: { items: [tag] } })),
    createTag: vi.fn(async () => ({ data: tag })),
    listAlbums: vi.fn(async () => ({ data: { items: [album] } })),
    getAlbum: vi.fn(async () => ({
      data: { album, items: { items: [] } },
    })),
    createAlbum: vi.fn(async () => ({ data: { album, items: { items: [] } } })),
    updateAlbum: vi.fn(async () => ({
      data: { album: { ...album, version: 4 }, items: { items: [] } },
    })),
    addAlbumItems: vi.fn(async () => ({
      data: { album: { ...album, version: 3, itemCount: 1 }, items: { items: [] } },
    })),
    removeAlbumItems: vi.fn(async () => ({
      data: { album: { ...album, version: 3 }, items: { items: [] } },
    })),
    bulkMutateAssets: vi.fn(async () => ({
      data: [{ assetId: 'asset-1', status: 'trashed', version: 4 }],
    })),
    restoreAssets: vi.fn(async () => ({
      data: {
        jobId: 'job-1',
        state: 'queued',
        submittedCount: 1,
        submittedAt: '2026-01-01T00:00:00Z',
      },
    })),
    ...overrides,
  };
}

function renderActions(
  options: {
    assets?: readonly AssetSummary[];
    client?: ReturnType<typeof makeClient>;
    canCurate?: boolean;
    onCurated?: () => void;
    onTrashed?: (ids: readonly string[]) => void;
    wait?: (milliseconds: number) => Promise<void>;
  } = {},
) {
  const client = options.client ?? makeClient();
  const user = userEvent.setup();
  render(
    <CurationActions
      assets={options.assets ?? [asset()]}
      canCurate={options.canCurate ?? true}
      client={client as never}
      createIdempotencyKey={() => 'key-1'}
      {...(options.wait ? { wait: options.wait } : {})}
      {...(options.onCurated ? { onCurated: options.onCurated } : {})}
      {...(options.onTrashed ? { onTrashed: options.onTrashed } : {})}
    />,
  );

  return { client, user };
}

describe('curation actions and the session', () => {
  it('is offered to a session that may manage metadata', () => {
    render(
      <SessionContext.Provider value={session(['assets.read', 'metadata.manage'])}>
        <CurationActions assets={[asset()]} client={makeClient() as never} />
      </SessionContext.Provider>,
    );

    expect(
      screen.getByRole('group', { name: 'Curation actions' }),
    ).toBeInTheDocument();
  });

  it('is withheld from a session that may only read', () => {
    render(
      <SessionContext.Provider value={session(['assets.read'])}>
        <CurationActions assets={[asset()]} client={makeClient() as never} />
      </SessionContext.Provider>,
    );

    expect(screen.queryByRole('group', { name: 'Curation actions' })).toBeNull();
  });
});

describe('curation actions', () => {
  it('is not offered to a session that may not curate', () => {
    renderActions({ canCurate: false });

    expect(screen.queryByRole('group', { name: 'Curation actions' })).toBeNull();
  });

  it('favourites the asset it was given with the version the API expects', async () => {
    const { client, user } = renderActions();

    const button = screen.getByRole('button', { name: 'Favorite' });
    expect(button).toHaveAttribute('aria-pressed', 'false');
    await user.click(button);

    expect(client.favoriteAsset).toHaveBeenCalledWith('asset-1', {
      idempotencyKey: 'key-1',
      ifMatch: '"v3"',
    });
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Favorited' })).toHaveAttribute(
        'aria-pressed',
        'true',
      ),
    );
    expect(screen.getByRole('status', { name: 'Curation result' })).toHaveTextContent(
      'Added to favorites: 1 image.',
    );
  });

  it('shows the change before the answer and takes it back when it fails', async () => {
    const client = makeClient({
      favoriteAsset: vi.fn(async () => {
        throw apiError(500);
      }),
    });
    const { user } = renderActions({ client });

    await user.click(screen.getByRole('button', { name: 'Favorite' }));

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Favorite' })).toHaveAttribute(
        'aria-pressed',
        'false',
      ),
    );
    expect(screen.getByRole('status', { name: 'Curation result' })).toHaveTextContent(
      'Added to favorites: no images. 1 image could not be updated.',
    );
  });

  it('reloads a stale version once and applies the change to it', async () => {
    const favoriteAsset = vi
      .fn()
      .mockRejectedValueOnce(apiError(412))
      .mockResolvedValue(detail(asset({ favorite: true, version: 8 })));
    const client = makeClient({
      favoriteAsset,
      getAsset: vi.fn(async () => detail(asset({ version: 7 }))),
    });
    const { user } = renderActions({ client });

    await user.click(screen.getByRole('button', { name: 'Favorite' }));

    await waitFor(() => expect(favoriteAsset).toHaveBeenCalledTimes(2));
    expect(client.getAsset).toHaveBeenCalledWith('asset-1');
    expect(favoriteAsset.mock.calls[1]?.[1]).toMatchObject({ ifMatch: '"v7"' });
    await waitFor(() =>
      expect(screen.getByRole('status', { name: 'Curation result' })).toHaveTextContent(
        'Added to favorites: 1 image. 1 image was refreshed before being applied.',
      ),
    );
  });

  it('reports every image in a selection the bulk route accepted', async () => {
    const bulkMutateAssets = vi.fn(async () => ({
      data: {
        jobId: 'job-1',
        state: 'queued',
        submittedCount: 2,
        submittedAt: '2026-01-01T00:00:00Z',
      },
    }));
    const client = makeClient({ bulkMutateAssets });
    const { user } = renderActions({
      assets: [asset({ id: 'a' }), asset({ id: 'b', title: 'Coastline' })],
      client,
    });

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    const panel = await screen.findByRole('group', { name: 'Tags' });
    await user.click(within(panel).getByRole('button', { name: /Coast/ }));

    await waitFor(() => expect(bulkMutateAssets).toHaveBeenCalledTimes(1));
    expect(client.addAssetTag).not.toHaveBeenCalled();
    const request = (bulkMutateAssets.mock.calls[0] as unknown[])[0] as {
      items: readonly unknown[];
    };
    expect(request.items).toEqual([
      { id: 'a', version: 3 },
      { id: 'b', version: 3 },
    ]);
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent('Tagged Coast: 2 images queued.'),
    );
    const outcomes = screen.getByRole('list', { name: 'Result for each image' });
    expect(within(outcomes).getAllByRole('listitem')).toHaveLength(2);
    expect(outcomes).toHaveTextContent('Coastline');
    expect(outcomes).toHaveTextContent('Queued');
  });

  it('reports every image in a selection the bulk route refused', async () => {
    const client = makeClient({
      bulkMutateAssets: vi.fn(async () => {
        throw apiError(500);
      }),
    });
    const { user } = renderActions({
      assets: [asset({ id: 'a' }), asset({ id: 'b', title: 'Coastline' })],
      client,
    });

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    const panel = await screen.findByRole('group', { name: 'Tags' });
    await user.click(within(panel).getByRole('button', { name: /Coast/ }));

    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent(
        'Tagged Coast: no images. 2 images could not be updated.',
      ),
    );
    const outcomes = screen.getByRole('list', { name: 'Result for each image' });
    expect(outcomes).toHaveTextContent('Could not be updated');
  });

  it('keeps the versioned path for the single image a viewer holds', async () => {
    const { client, user } = renderActions();

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    const panel = await screen.findByRole('group', { name: 'Tags' });
    await user.click(within(panel).getByRole('button', { name: /Coast/ }));

    await waitFor(() =>
      expect(client.addAssetTag).toHaveBeenCalledWith('asset-1', 'tag-1', {
        idempotencyKey: 'key-1',
        ifMatch: '"v3"',
      }),
    );
    expect(client.bulkMutateAssets).not.toHaveBeenCalled();
    expect(
      screen.getByRole('status', { name: 'Curation result' }),
    ).toHaveTextContent('Tagged Coast: 1 image.');
  });

  it('creates a tag and applies it in the same step', async () => {
    const { client, user } = renderActions();

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    const panel = await screen.findByRole('group', { name: 'Tags' });
    await user.click(within(panel).getByLabelText('New tag name'));
    await user.paste('Harbour');
    await user.click(within(panel).getByRole('button', { name: 'Create and add' }));

    await waitFor(() =>
      expect(client.createTag).toHaveBeenCalledWith(
        { name: 'Harbour' },
        { idempotencyKey: 'key-1' },
      ),
    );
    await waitFor(() => expect(client.addAssetTag).toHaveBeenCalled());
  });

  it('adds the selection to an album and gives an empty album its cover', async () => {
    const { client, user } = renderActions({
      assets: [
        asset({
          renditions: [
            {
              kind: 'grid' as const,
              path: '/media/a.png',
              width: 10,
              height: 10,
              contentType: 'image/png',
            },
          ],
        }),
      ],
    });

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByRole('button', { name: 'Add to Summer' }));

    await waitFor(() =>
      expect(client.addAlbumItems).toHaveBeenCalledWith(
        'album-1',
        { items: [{ id: 'asset-1', version: 3 }] },
        { idempotencyKey: 'key-1', ifMatch: '"v2"' },
      ),
    );
    await waitFor(() =>
      expect(client.updateAlbum).toHaveBeenCalledWith(
        'album-1',
        { coverAssetId: 'asset-1' },
        { idempotencyKey: 'key-1', ifMatch: '"v3"' },
      ),
    );
    expect(screen.getByRole('status', { name: 'Curation result' })).toHaveTextContent(
      'Added to Summer: 1 image.',
    );
  });

  it('leaves an album that already has a cover alone', async () => {
    const covered = {
      ...album,
      cover: {
        kind: 'grid' as const,
        path: '/media/cover.png',
        width: 10,
        height: 10,
        contentType: 'image/png',
      },
    };
    const client = makeClient({
      listAlbums: vi.fn(async () => ({ data: { items: [covered] } })),
    });
    const { user } = renderActions({ client });

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByRole('button', { name: 'Add to Summer' }));

    await waitFor(() => expect(client.addAlbumItems).toHaveBeenCalled());
    expect(client.updateAlbum).not.toHaveBeenCalled();
  });

  it('reloads a stale album version once before adding', async () => {
    const addAlbumItems = vi
      .fn()
      .mockRejectedValueOnce(apiError(412))
      .mockResolvedValue({
        data: { album: { ...album, version: 9 }, items: { items: [] } },
      });
    const client = makeClient({
      addAlbumItems,
      getAlbum: vi.fn(async () => ({
        data: { album: { ...album, version: 8 }, items: { items: [] } },
      })),
    });
    const { user } = renderActions({ client });

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByRole('button', { name: 'Add to Summer' }));

    await waitFor(() => expect(addAlbumItems).toHaveBeenCalledTimes(2));
    expect(addAlbumItems.mock.calls[1]?.[2]).toMatchObject({ ifMatch: '"v8"' });
  });

  it('reloads the asset versions too when an album change is refused', async () => {
    const addAlbumItems = vi
      .fn()
      .mockRejectedValueOnce(apiError(412))
      .mockResolvedValue({
        data: { album: { ...album, version: 9 }, items: { items: [] } },
      });
    const client = makeClient({
      addAlbumItems,
      getAlbum: vi.fn(async () => ({
        data: { album: { ...album, version: 8 }, items: { items: [] } },
      })),
      getAsset: vi.fn(async (id: string) =>
        detail(asset({ id, version: 11 })),
      ),
    });
    const { user } = renderActions({ client });

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByRole('button', { name: 'Add to Summer' }));

    await waitFor(() => expect(addAlbumItems).toHaveBeenCalledTimes(2));
    expect(client.getAsset).toHaveBeenCalledWith('asset-1');
    expect((addAlbumItems.mock.calls[1] as unknown[])[1]).toEqual({
      items: [{ id: 'asset-1', version: 11 }],
    });
  });

  it('never asks the API for more items than one batch may carry', async () => {
    const many = Array.from({ length: 250 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const client = makeClient({
      bulkMutateAssets: vi.fn(async (request: { items: readonly unknown[] }) => ({
        data: request.items.map((item) => ({
          assetId: (item as { id: string }).id,
          status: 'trashed',
          version: 4,
        })),
      })),
    });
    const { user } = renderActions({ assets: many, client });

    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Move to trash' }));

    await waitFor(() =>
      expect(client.bulkMutateAssets).toHaveBeenCalledTimes(2),
    );
    for (const call of client.bulkMutateAssets.mock.calls as unknown[][]) {
      const request = call[0] as { items: readonly unknown[] };
      expect(request.items.length).toBeLessThanOrEqual(200);
    }
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent('Moved to trash: 250 images.'),
    );
  });

  it('splits an album change into batches the API accepts', async () => {
    const many = Array.from({ length: 250 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const { client, user } = renderActions({ assets: many });

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByRole('button', { name: 'Add to Summer' }));

    await waitFor(() => expect(client.addAlbumItems).toHaveBeenCalledTimes(2));
    for (const call of client.addAlbumItems.mock.calls as unknown[][]) {
      const request = call[1] as { items: readonly unknown[] };
      expect(request.items.length).toBeLessThanOrEqual(200);
    }
    // The second batch carries the version the first one produced.
    const second = client.addAlbumItems.mock.calls[1] as unknown[];
    expect(second[2]).toMatchObject({ ifMatch: '"v3"' });
  });

  it('asks before moving images to the trash and can undo it', async () => {
    const onTrashed = vi.fn();
    const { client, user } = renderActions({ onTrashed });

    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Move 1 image to trash?',
    });
    expect(within(dialog).getByRole('button', { name: 'Move to trash' })).toHaveFocus();
    await user.click(within(dialog).getByLabelText('Reason (optional)'));
    await user.paste('duplicate');
    await user.click(within(dialog).getByRole('button', { name: 'Move to trash' }));

    await waitFor(() =>
      expect(client.bulkMutateAssets).toHaveBeenCalledWith(
        {
          items: [{ id: 'asset-1', version: 3 }],
          action: { kind: 'trash', reason: 'duplicate' },
        },
        { idempotencyKey: 'key-1' },
      ),
    );
    expect(onTrashed).toHaveBeenCalledWith(
      ['asset-1'],
      [{ id: 'asset-1', version: 4 }],
    );
    await waitFor(() =>
      expect(screen.getByRole('status', { name: 'Curation result' })).toHaveTextContent(
        'Moved to trash: 1 image.',
      ),
    );

    await user.click(screen.getByRole('button', { name: 'Undo move to trash' }));
    await waitFor(() =>
      expect(client.restoreAssets).toHaveBeenCalledWith(
        { items: [{ id: 'asset-1', version: 4 }] },
        { idempotencyKey: 'key-1' },
      ),
    );
    expect(screen.getByRole('status', { name: 'Curation result' })).toHaveTextContent(
      'Restore queued for 1 image.',
    );
  });

  it('takes focus to the undo and keeps the same live region', async () => {
    const { user } = renderActions();

    const before = screen.getByRole('status', { name: 'Curation result' });
    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Move to trash' }));

    const undo = await screen.findByRole('button', {
      name: 'Undo move to trash',
    });
    await waitFor(() => expect(undo).toHaveFocus());
    // The same element carried the message, so it is announced as an update.
    expect(screen.getByRole('status', { name: 'Curation result' })).toBe(before);
    expect(before).toHaveTextContent('Moved to trash: 1 image.');
  });

  it('traps the keyboard inside the confirmation', async () => {
    const { user } = renderActions();

    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog');
    for (let step = 0; step < 3; step += 1) {
      await user.tab();
      expect(dialog).toContainElement(
        document.activeElement as HTMLElement | null,
      );
    }
  });

  it('marks Escape handled so a page behind it does not act on the key', async () => {
    const seen: boolean[] = [];
    const listener = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        seen.push(event.defaultPrevented);
      }
    };
    window.addEventListener('keydown', listener);
    try {
      const { user } = renderActions();
      await user.click(screen.getByRole('button', { name: 'Tags' }));
      await screen.findByRole('group', { name: 'Tags' });
      await user.keyboard('{Escape}');

      expect(seen).toEqual([true]);
    } finally {
      window.removeEventListener('keydown', listener);
    }
  });

  it('keeps the images when the confirmation is dismissed', async () => {
    const { client, user } = renderActions();

    const trigger = screen.getByRole('button', { name: 'Move to trash' });
    await user.click(trigger);
    await screen.findByRole('dialog');
    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(client.bulkMutateAssets).not.toHaveBeenCalled();
    expect(trigger).toHaveFocus();
  });

  it('never offers the trash for an image the API would refuse', () => {
    renderActions({ assets: [asset({ status: 'processing' })] });

    expect(screen.getByRole('button', { name: 'Move to trash' })).toBeDisabled();
  });

  it('closes a panel with Escape and returns focus to the control that opened it', async () => {
    const { user } = renderActions();

    const trigger = screen.getByRole('button', { name: 'Tags' });
    await user.click(trigger);
    await screen.findByRole('group', { name: 'Tags' });
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    await user.keyboard('{Escape}');

    await waitFor(() =>
      expect(screen.queryByRole('group', { name: 'Tags' })).toBeNull(),
    );
    expect(trigger).toHaveFocus();
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  });

  it('offers a retry when the tag list cannot be read', async () => {
    const listTags = vi
      .fn()
      .mockRejectedValueOnce(apiError(500))
      .mockResolvedValue({ data: { items: [tag] } });
    const { user } = renderActions({ client: makeClient({ listTags }) });

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    expect(
      await screen.findByRole('heading', { name: 'Tags could not be loaded' }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(
      await screen.findByRole('button', { name: /Coast/ }),
    ).toBeInTheDocument();
  });

  it('says when there are no albums to add to yet', async () => {
    const client = makeClient({
      listAlbums: vi.fn(async () => ({ data: { items: [] } })),
    });
    const { user } = renderActions({ client });

    await user.click(screen.getByRole('button', { name: 'Albums' }));

    expect(
      await screen.findByRole('heading', { name: 'No albums yet' }),
    ).toBeInTheDocument();
  });

  it('reports a partial trash of a mixed selection', async () => {
    const client = makeClient({
      bulkMutateAssets: vi.fn(async () => ({
        data: [{ assetId: 'a', status: 'trashed', version: 4 }],
      })),
    });
    const { user } = renderActions({
      assets: [
        asset({ id: 'a' }),
        asset({ id: 'b', title: 'Still processing', status: 'processing' }),
      ],
      client,
    });

    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Move 1 image to trash?',
    });
    await user.click(within(dialog).getByRole('button', { name: 'Move to trash' }));

    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent('Moved to trash: 1 image.'),
    );
    expect(
      screen.getByRole('button', { name: 'Undo move to trash' }),
    ).toBeInTheDocument();
  });

  it('favourites a large selection through the bulk route in batches', async () => {
    const many = Array.from({ length: 450 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const bulkMutateAssets = vi.fn(async () => ({
      data: {
        jobId: 'job-1',
        state: 'queued',
        submittedCount: 200,
        submittedAt: '2026-01-01T00:00:00Z',
      },
    }));
    const client = makeClient({ bulkMutateAssets });
    const { user } = renderActions({ assets: many, client });

    await user.click(screen.getByRole('button', { name: 'Favorite' }));

    await waitFor(() => expect(bulkMutateAssets).toHaveBeenCalledTimes(3));
    expect(client.favoriteAsset).not.toHaveBeenCalled();
    for (const call of bulkMutateAssets.mock.calls as unknown[][]) {
      const request = call[0] as {
        items: readonly unknown[];
        action: unknown;
      };
      expect(request.items.length).toBeLessThanOrEqual(200);
      expect(request.action).toEqual({ kind: 'setFavorite', favorite: true });
    }
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent('Added to favorites: 450 images queued.'),
    );
  });

  it('tags a large selection through the bulk route', async () => {
    const many = Array.from({ length: 450 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const bulkMutateAssets = vi.fn(async () => ({
      data: {
        jobId: 'job-1',
        state: 'queued',
        submittedCount: 200,
        submittedAt: '2026-01-01T00:00:00Z',
      },
    }));
    const client = makeClient({ bulkMutateAssets });
    const { user } = renderActions({ assets: many, client });

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    const panel = await screen.findByRole('group', { name: 'Tags' });
    await user.click(within(panel).getByRole('button', { name: /Coast/ }));

    await waitFor(() => expect(bulkMutateAssets).toHaveBeenCalledTimes(3));
    expect(client.addAssetTag).not.toHaveBeenCalled();
    const first = (bulkMutateAssets.mock.calls[0] as unknown[])[0] as {
      action: unknown;
    };
    expect(first.action).toEqual({ kind: 'addTag', tagId: 'tag-1' });
  });

  it('waits the delay a rate limit asked for instead of flooding it', async () => {
    const many = Array.from({ length: 450 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const throttled = new VistaraApiError(429, {
      type: 'about:blank',
      title: 'rate_limited',
      status: 429,
      code: 'rate_limited',
      errors: {},
      retryAfterSeconds: 2,
    } as never);
    const bulkMutateAssets = vi
      .fn()
      .mockRejectedValueOnce(throttled)
      .mockResolvedValue({
        data: {
          jobId: 'job-1',
          state: 'queued',
          submittedCount: 200,
          submittedAt: '2026-01-01T00:00:00Z',
        },
      });
    const waits: number[] = [];
    const { user } = renderActions({
      assets: many,
      client: makeClient({ bulkMutateAssets }),
      wait: async (milliseconds: number) => {
        waits.push(milliseconds);
      },
    });

    await user.click(screen.getByRole('button', { name: 'Favorite' }));

    // Three batches plus exactly one retry of the refused batch.
    await waitFor(() => expect(bulkMutateAssets).toHaveBeenCalledTimes(4));
    expect(waits).toEqual([2000]);
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent('Added to favorites: 450 images queued.'),
    );
  });

  it('gives up on a batch the API keeps refusing without retrying it again', async () => {
    const many = Array.from({ length: 250 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const throttled = new VistaraApiError(429, {
      type: 'about:blank',
      title: 'rate_limited',
      status: 429,
      code: 'rate_limited',
      errors: {},
      retryAfterSeconds: 1,
    } as never);
    const bulkMutateAssets = vi
      .fn()
      .mockRejectedValueOnce(throttled)
      .mockRejectedValueOnce(throttled)
      .mockResolvedValue({
        data: {
          jobId: 'job-1',
          state: 'queued',
          submittedCount: 50,
          submittedAt: '2026-01-01T00:00:00Z',
        },
      });
    const { user } = renderActions({
      assets: many,
      client: makeClient({ bulkMutateAssets }),
      wait: async () => undefined,
    });

    await user.click(screen.getByRole('button', { name: 'Favorite' }));

    await waitFor(() => expect(bulkMutateAssets).toHaveBeenCalledTimes(3));
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent(
        'Added to favorites: 50 images queued. 200 images could not be updated.',
      ),
    );
  });

  it('restores every image it trashed in batches the API accepts', async () => {
    const many = Array.from({ length: 250 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const restoreAssets = vi.fn(
      async (request: { items: readonly unknown[] }) => ({
        data: {
          jobId: 'job-1',
          state: 'queued',
          submittedCount: request.items.length,
          submittedAt: '2026-01-01T00:00:00Z',
        },
      }),
    );
    const client = makeClient({
      bulkMutateAssets: vi.fn(async (request: { items: readonly unknown[] }) => ({
        data: request.items.map((item) => ({
          assetId: (item as { id: string }).id,
          status: 'trashed',
          version: 4,
        })),
      })),
      restoreAssets,
    });
    const { user } = renderActions({ assets: many, client });

    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Move to trash' }));
    const undo = await screen.findByRole('button', {
      name: 'Undo move to trash',
    });
    await user.click(undo);

    await waitFor(() => expect(restoreAssets).toHaveBeenCalledTimes(2));
    for (const call of restoreAssets.mock.calls as unknown[][]) {
      const request = call[0] as { items: readonly unknown[] };
      expect(request.items.length).toBeLessThanOrEqual(200);
    }
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent('Restore queued for 250 images.'),
    );
  });

  it('keeps what an earlier album batch committed when a later one fails', async () => {
    const many = Array.from({ length: 250 }, (_, index) =>
      asset({ id: `asset-${index}`, title: `Image ${index}` }),
    );
    const addAlbumItems = vi
      .fn()
      .mockResolvedValueOnce({
        data: {
          album: { ...album, version: 3, itemCount: 200 },
          items: { items: [] },
        },
      })
      .mockRejectedValue(apiError(500));
    const onCurated = vi.fn();
    const { user } = renderActions({
      assets: many,
      client: makeClient({ addAlbumItems }),
      onCurated,
    });

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByRole('button', { name: 'Add to Summer' }));

    await waitFor(() => expect(addAlbumItems).toHaveBeenCalledTimes(2));
    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Curation result' }),
      ).toHaveTextContent(
        'Added to Summer: 200 images. 50 images could not be updated.',
      ),
    );
    expect(onCurated).toHaveBeenCalled();
  });

  it('lists a tag the API replayed only once', async () => {
    const { client, user } = renderActions();

    await user.click(screen.getByRole('button', { name: 'Tags' }));
    const panel = await screen.findByRole('group', { name: 'Tags' });
    await user.click(within(panel).getByLabelText('New tag name'));
    await user.paste('Coast');
    await user.click(within(panel).getByRole('button', { name: 'Create and add' }));

    await waitFor(() => expect(client.createTag).toHaveBeenCalled());
    await waitFor(() =>
      expect(
        within(screen.getByRole('group', { name: 'Tags' })).getAllByRole(
          'button',
          { name: /Coast/ },
        ),
      ).toHaveLength(1),
    );
  });

  it('lists an album the API replayed only once', async () => {
    const { client, user } = renderActions();

    await user.click(screen.getByRole('button', { name: 'Albums' }));
    const panel = await screen.findByRole('group', { name: 'Albums' });
    await user.click(within(panel).getByLabelText('New album name'));
    await user.paste('Summer');
    await user.click(within(panel).getByRole('button', { name: 'Create and add' }));

    await waitFor(() => expect(client.createAlbum).toHaveBeenCalled());
    await waitFor(() =>
      expect(
        within(screen.getByRole('group', { name: 'Albums' })).getAllByRole(
          'button',
          { name: 'Add to Summer' },
        ),
      ).toHaveLength(1),
    );
  });

  it('reports how many images the actions apply to', () => {
    renderActions({ assets: [asset({ id: 'a' }), asset({ id: 'b' })] });

    expect(
      screen.getByRole('group', { name: 'Curation actions' }),
    ).toHaveTextContent('2 images selected');
  });
});
