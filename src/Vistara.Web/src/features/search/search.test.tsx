import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { AssetSummary, CursorPage } from '../../api/generated';
import { SearchView, type SearchClient } from './SearchView';

function asset(id: string, title: string): AssetSummary {
  return {
    id,
    title,
    status: 'ready',
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/jpeg',
    format: 'jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 240_000,
    importedAt: '2026-02-01T10:00:00Z',
    updatedAt: '2026-02-01T10:00:00Z',
    capturedAt: '2026-01-31T18:22:00Z',
    favorite: false,
    tags: [],
    renditions: [
      {
        kind: 'thumbnail',
        path: `/media/thumb/${id}.webp`,
        width: 400,
        height: 300,
        contentType: 'image/webp',
      },
      {
        kind: 'display',
        path: `/media/display/${id}.webp`,
        width: 1200,
        height: 900,
        contentType: 'image/webp',
      },
    ],
    version: 1,
  };
}

function page(
  items: readonly AssetSummary[],
  nextCursor?: string,
): { data: CursorPage<AssetSummary> } {
  return { data: nextCursor ? { items, nextCursor } : { items } };
}

type ListAssets = SearchClient['listAssets'];

function renderSearch(listAssets: ListAssets, entry = '/search?q=harbour') {
  const router = createMemoryRouter(
    [
      { path: '/search', element: <SearchView client={{ listAssets }} /> },
      { path: '/assets/:assetId', element: <h1>Asset</h1> },
    ],
    { initialEntries: [entry] },
  );

  render(<RouterProvider router={router} />);
  return router;
}

describe('search', () => {
  it('invites a first search without calling the API', () => {
    const listAssets = vi.fn<ListAssets>();
    renderSearch(listAssets, '/search');

    expect(
      screen.getByRole('heading', { name: 'Search' }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/Search titles, descriptions, and tags/),
    ).toBeInTheDocument();
    expect(listAssets).not.toHaveBeenCalled();
  });

  it('runs the search described by the address and lists matches', async () => {
    const listAssets = vi.fn(async () =>
      page([asset('a1', 'Harbour lights'), asset('a2', 'Harbour dawn')]),
    );
    renderSearch(listAssets, '/search?q=harbour&favorite=true');

    const results = await screen.findByRole('list', { name: 'Search results' });

    expect(
      within(results).getByRole('link', { name: /Harbour lights/ }),
    ).toHaveAttribute('href', '/assets/a1');
    expect(listAssets).toHaveBeenCalledWith(
      expect.objectContaining({ search: 'harbour', favorite: true }),
    );
    expect(screen.getByRole('status')).toHaveTextContent('2 results');

    const [image] = results.querySelectorAll('img');
    expect(image).toHaveAttribute('sizes');
    expect(image).toHaveAttribute('srcset');
    expect(image).toHaveAttribute('alt', '');
  });

  it('keeps the query and filters addressable after submitting', async () => {
    const user = userEvent.setup();
    const listAssets = vi.fn(async () => page([asset('a1', 'Harbour lights')]));
    const router = renderSearch(listAssets, '/search');

    await user.type(screen.getByLabelText('Search your library'), 'harbour');
    await user.click(screen.getByLabelText('Favorites only'));
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() =>
      expect(router.state.location.search).toBe('?q=harbour&favorite=true'),
    );
  });

  it('reports an empty result with a way to widen the search', async () => {
    const user = userEvent.setup();
    const listAssets = vi.fn(async () => page([]));
    const router = renderSearch(listAssets, '/search?q=harbour&favorite=true');

    expect(
      await screen.findByRole('heading', { name: 'No images match' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Clear filters' }));

    await waitFor(() => expect(router.state.location.search).toBe(''));
  });

  it('offers a retry when the search fails', async () => {
    const user = userEvent.setup();
    const listAssets = vi
      .fn<ListAssets>()
      .mockRejectedValueOnce(
        new VistaraApiError(503, {
          type: 'about:blank',
          title: 'Unavailable',
          status: 503,
          code: 'unavailable',
          errors: {},
        }),
      )
      .mockResolvedValueOnce(page([asset('a1', 'Harbour lights')]));
    renderSearch(listAssets, '/search?q=harbour');

    expect(
      await screen.findByRole('heading', { name: 'Search failed' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(
      await screen.findByRole('link', { name: /Harbour lights/ }),
    ).toBeInTheDocument();
  });

  it('continues from the returned cursor without losing earlier matches', async () => {
    const user = userEvent.setup();
    const listAssets = vi
      .fn<ListAssets>()
      .mockResolvedValueOnce(page([asset('a1', 'Harbour lights')], 'cursor-2'))
      .mockResolvedValueOnce(page([asset('a2', 'Harbour dawn')]));
    renderSearch(listAssets, '/search?q=harbour');

    await user.click(
      await screen.findByRole('button', { name: 'Show more results' }),
    );

    expect(
      await screen.findByRole('link', { name: /Harbour dawn/ }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: /Harbour lights/ }),
    ).toBeInTheDocument();
    expect(listAssets).toHaveBeenLastCalledWith(
      expect.objectContaining({ cursor: 'cursor-2' }),
    );
    expect(
      screen.queryByRole('button', { name: 'Show more results' }),
    ).not.toBeInTheDocument();
  });

  it('announces the pending search while results load', async () => {
    const listAssets = vi.fn(
      () => new Promise<{ data: CursorPage<AssetSummary> }>(() => {}),
    );
    renderSearch(listAssets, '/search?q=harbour');

    expect(await screen.findByRole('status')).toHaveTextContent('Searching');
  });
});
