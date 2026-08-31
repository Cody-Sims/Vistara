import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  MemoryRouter,
  Route,
  Routes,
  useLocation,
} from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { AssetDetail, AssetSummary } from '../../api/generated';
import { ViewerPage, type ViewerDataSource } from './ViewerPage';

function asset(status: AssetSummary['status'] = 'ready'): AssetSummary {
  return {
    id: 'asset-7',
    title: 'Moonrise',
    description: 'Moonrise above a dark ridge',
    status,
    visibility: 'private',
    revisionNumber: 1,
    contentType: 'image/jpeg',
    format: 'jpeg',
    width: 2400,
    height: 1600,
    sizeBytes: 2_400_000,
    capturedAt: '2026-04-03T20:15:00Z',
    importedAt: '2026-04-04T09:00:00Z',
    updatedAt: '2026-04-04T09:00:00Z',
    favorite: true,
    tags: [{ id: 'night', name: 'Night' }],
    renditions:
      status === 'ready'
        ? [
            {
              kind: 'grid',
              path: '/media/moonrise-1024.webp',
              width: 1024,
              height: 683,
              contentType: 'image/webp',
            },
            {
              kind: 'viewer',
              path: '/media/moonrise-2400.webp',
              width: 2400,
              height: 1600,
              contentType: 'image/webp',
            },
          ]
        : [],
    version: 1,
  };
}

function detail(status: AssetSummary['status'] = 'ready'): AssetDetail {
  return {
    asset: asset(status),
    metadata: {
      capturedAt: '2026-04-03T20:15:00Z',
      cameraMake: 'Vistara Camera Co.',
      cameraModel: 'Summit',
      restrictedMetadataAvailable: true,
    },
    albums: [],
  };
}

function LocationProbe() {
  const location = useLocation();
  return <output aria-label="location">{location.pathname + location.search}</output>;
}

function renderViewer(
  dataSource: ViewerDataSource,
  initialEntry:
    | string
    | { pathname: string; state?: Record<string, unknown> } = '/assets/asset-7',
) {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route
            path="/assets/:assetId"
            element={
              <ViewerPage
                dataSource={dataSource}
                neighborIds={{ previous: 'asset-6', next: 'asset-8' }}
              />
            }
          />
          <Route path="/library" element={<h1>Library restored</h1>} />
        </Routes>
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('asset viewer', () => {
  it('renders a stable asset route with one responsive high-priority image', async () => {
    const dataSource = {
      getAsset: vi.fn(async () => ({ data: detail() })),
    };

    renderViewer(dataSource);

    expect(screen.getByText('Loading asset…')).toBeInTheDocument();
    expect(
      await screen.findByRole('heading', { name: 'Moonrise' }),
    ).toBeInTheDocument();
    const image = screen.getByRole('img', {
      name: 'Moonrise above a dark ridge',
    });
    expect(image).toHaveAttribute('fetchpriority', 'high');
    expect(image).toHaveAttribute('sizes', '100vw');
    expect(screen.getByText('Vistara Camera Co. Summit')).toBeInTheDocument();
    expect(dataSource.getAsset).toHaveBeenCalledWith('asset-7');
  });

  it.each([
    ['processing', 'Preview is still processing'],
    ['failed', 'Preview processing failed'],
  ] as const)('shows the %s state without inventing an image', async (status, text) => {
    renderViewer({
      getAsset: async () => ({ data: detail(status) }),
    });

    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('supports keyboard navigation and returns to the exact library address', async () => {
    const user = userEvent.setup();
    renderViewer(
      { getAsset: async () => ({ data: detail() }) },
      {
        pathname: '/assets/asset-7',
        state: { libraryAddress: '/library?q=moon&view=list' },
      },
    );
    await screen.findByRole('heading', { name: 'Moonrise' });

    await user.keyboard('{ArrowRight}');
    expect(screen.getByLabelText('location')).toHaveTextContent('/assets/asset-8');

    await user.keyboard('{Escape}');
    expect(
      await screen.findByRole('heading', { name: 'Library restored' }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('location')).toHaveTextContent(
      '/library?q=moon&view=list',
    );
  });

  it('offers a recoverable error state', async () => {
    const getAsset = vi
      .fn<ViewerDataSource['getAsset']>()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce({ data: detail() });
    const user = userEvent.setup();

    renderViewer({ getAsset });
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Could not load this asset',
    );

    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(
      await screen.findByRole('heading', { name: 'Moonrise' }),
    ).toBeInTheDocument();
  });
});
