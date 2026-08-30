import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type {
  AssetSummary,
  TimelinePage,
  TimelineQuery,
} from '../../api/generated';
import { LibraryPage, type LibraryDataSource } from './LibraryPage';
import { createLibraryRestorationStore } from './libraryRestoration';

function asset(index: number, status: AssetSummary['status'] = 'ready') {
  return {
    id: `asset-${index}`,
    title: `Photo ${index}`,
    description: `Accessible description ${index}`,
    status,
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/jpeg',
    format: 'jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 42_000,
    capturedAt: '2026-06-10T12:00:00Z',
    importedAt: '2026-06-11T12:00:00Z',
    updatedAt: '2026-06-11T12:00:00Z',
    favorite: false,
    tags: [],
    renditions:
      status === 'ready'
        ? [
            {
              kind: 'thumbnail',
              path: `/media/${index}-512.jpg`,
              width: 512,
              height: 384,
              contentType: 'image/jpeg',
            },
            {
              kind: 'preview',
              path: `/media/${index}-1024.webp`,
              width: 1024,
              height: 768,
              contentType: 'image/webp',
            },
          ]
        : [],
    version: 1,
  } satisfies AssetSummary;
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

function LocationProbe() {
  const location = useLocation();
  return <output aria-label="location">{location.search}</output>;
}

function renderLibrary(
  dataSource: LibraryDataSource,
  initialEntry = '/library',
  restorationStore?: ReturnType<typeof createLibraryRestorationStore>,
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <LibraryPage
          dataSource={dataSource}
          layout={{ columns: 4, viewportHeight: 700 }}
          restorationStore={restorationStore}
        />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('library page', () => {
  it('renders a semantic virtualized timeline within DOM and image priority budgets', async () => {
    const dataSource = {
      getTimeline: vi.fn(async () => ({
        data: page(Array.from({ length: 800 }, (_, index) => asset(index))),
      })),
    };

    const { container } = renderLibrary(dataSource);

    expect(screen.getByText('Loading library…')).toBeInTheDocument();
    expect(
      await screen.findByRole('heading', { name: 'June 10, 2026' }),
    ).toBeInTheDocument();

    const images = container.querySelectorAll('img');
    expect(images.length).toBeGreaterThan(0);
    expect(images.length).toBeLessThanOrEqual(400);
    expect(
      container.querySelectorAll('img[fetchpriority="high"]'),
    ).toHaveLength(1);
    expect(images[0]).toHaveAttribute(
      'sizes',
      '(max-width: 30rem) 50vw, (max-width: 64rem) 33vw, min(20vw, 20rem)',
    );
    expect(container.querySelector('[role="grid"]')).not.toBeInTheDocument();
  });

  it('keeps search, sort, grouping, status, and view controls URL-addressable', async () => {
    const requested: TimelineQuery[] = [];
    const dataSource = {
      getTimeline: vi.fn(async (query: TimelineQuery) => {
        requested.push(query);
        return { data: page([asset(1)]) };
      }),
    };
    const user = userEvent.setup();

    renderLibrary(dataSource, '/library?q=snow&view=list');
    const search = await screen.findByRole('searchbox', {
      name: 'Search library',
    });
    expect(search).toHaveValue('snow');
    expect(await screen.findByText('1,600 × 1,200')).toBeInTheDocument();
    expect(screen.getByText('JPEG')).toBeInTheDocument();

    await user.clear(search);
    await user.type(search, 'portraits');
    await user.selectOptions(screen.getByLabelText('Sort library'), 'title');
    await user.selectOptions(screen.getByLabelText('Timeline grouping'), 'month');
    await user.click(screen.getByRole('checkbox', { name: 'Processing' }));
    await user.click(screen.getByRole('button', { name: 'Grid view' }));
    await user.click(screen.getByRole('button', { name: 'Apply search' }));

    await waitFor(() =>
      expect(screen.getByLabelText('location')).toHaveTextContent(
        '?q=portraits&sort=title&group=month&status=processing',
      ),
    );
    expect(requested.at(-1)).toMatchObject({
      search: 'portraits',
      sort: 'title',
      groupBy: 'month',
      statuses: ['processing'],
    });
  });

  it('supports visible touch controls and shift-range keyboard selection', async () => {
    const dataSource = {
      getTimeline: vi.fn(async () => ({
        data: page(Array.from({ length: 8 }, (_, index) => asset(index))),
      })),
    };
    const user = userEvent.setup();

    renderLibrary(dataSource);
    const checkboxes = await screen.findAllByRole('checkbox', {
      name: /Select Photo/,
    });

    await user.click(checkboxes[1] as HTMLElement);
    await user.keyboard('{Shift>}');
    await user.click(checkboxes[4] as HTMLElement);
    await user.keyboard('{/Shift}');

    expect(screen.getByRole('status', { name: 'Selection status' })).toHaveTextContent(
      '4 selected',
    );
    expect(
      screen.getByRole('button', { name: 'Select visible' }),
    ).toBeVisible();
  });

  it('restores the virtual scroll window and focused asset for browser Back', async () => {
    const values = new Map<string, string>();
    const restorationStore = createLibraryRestorationStore({
      getItem: (key) => values.get(key) ?? null,
      setItem: (key, value) => values.set(key, value),
      removeItem: (key) => values.delete(key),
    });
    restorationStore.save('/library', {
      scrollTop: 4_000,
      focusedAssetId: 'asset-60',
    });

    renderLibrary(
      {
        getTimeline: async () => ({
          data: page(Array.from({ length: 200 }, (_, index) => asset(index))),
        }),
      },
      '/library',
      restorationStore,
    );

    await screen.findByRole('heading', { name: 'June 10, 2026' });
    await waitFor(() =>
      expect(screen.getByLabelText('Library timeline')).toHaveProperty(
        'scrollTop',
        4_000,
      ),
    );
    expect(await screen.findByText('Photo 52')).toBeInTheDocument();
    expect(screen.getByText('Photo 60').closest('a')).toHaveFocus();
    expect(
      document.querySelector('img[fetchpriority="high"]'),
    ).toHaveAccessibleName('Accessible description 60');
  });

  it('selects viewport assets without including overscan or a pinned focus row', async () => {
    const values = new Map<string, string>();
    const restorationStore = createLibraryRestorationStore({
      getItem: (key) => values.get(key) ?? null,
      setItem: (key, value) => values.set(key, value),
      removeItem: (key) => values.delete(key),
    });
    restorationStore.save('/library', {
      scrollTop: 0,
      focusedAssetId: 'asset-100',
    });
    const user = userEvent.setup();

    renderLibrary(
      {
        getTimeline: async () => ({
          data: page(Array.from({ length: 200 }, (_, index) => asset(index))),
        }),
      },
      '/library',
      restorationStore,
    );
    await screen.findByText('Photo 100');

    await user.click(screen.getByRole('button', { name: 'Select visible' }));

    expect(screen.getByRole('status', { name: 'Selection status' })).toHaveTextContent(
      '12 selected',
    );
  });

  it('loads cursor pages without replacing the current timeline', async () => {
    const getTimeline = vi
      .fn<LibraryDataSource['getTimeline']>()
      .mockResolvedValueOnce({
        data: { ...page([asset(1)]), nextCursor: 'next-page' },
      })
      .mockResolvedValueOnce({ data: page([asset(2)]) });
    const user = userEvent.setup();

    renderLibrary({ getTimeline });
    expect(await screen.findByText('Photo 1')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Load more images' }));

    expect(await screen.findByText('Photo 2')).toBeInTheDocument();
    expect(getTimeline).toHaveBeenLastCalledWith(
      expect.objectContaining({ cursor: 'next-page' }),
    );
  });

  it('distinguishes processing, empty, and recoverable error states', async () => {
    const dataSource = {
      getTimeline: vi
        .fn<LibraryDataSource['getTimeline']>()
        .mockResolvedValueOnce({ data: page([asset(1, 'processing')]) })
        .mockRejectedValueOnce(new Error('network unavailable')),
    };
    const processingRender = renderLibrary(dataSource);
    expect(await screen.findByText('Processing preview')).toBeInTheDocument();
    processingRender.unmount();

    const emptyRender = render(
      <QueryClientProvider
        client={
          new QueryClient({ defaultOptions: { queries: { retry: false } } })
        }
      >
        <MemoryRouter initialEntries={['/library?q=empty']}>
          <LibraryPage
            dataSource={{
              getTimeline: async () => ({ data: { groups: [] } }),
            }}
            layout={{ columns: 4, viewportHeight: 700 }}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(await screen.findByText('No images found')).toBeInTheDocument();
    emptyRender.unmount();

    render(
      <QueryClientProvider
        client={
          new QueryClient({ defaultOptions: { queries: { retry: false } } })
        }
      >
        <MemoryRouter initialEntries={['/library?q=error']}>
          <LibraryPage
            dataSource={dataSource}
            layout={{ columns: 4, viewportHeight: 700 }}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Could not load the library',
    );
  });
});
