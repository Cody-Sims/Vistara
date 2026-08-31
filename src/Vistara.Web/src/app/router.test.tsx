import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createAppRouter } from './router';

describe('application shell', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('redirects home to an accessible library frame', async () => {
    const user = userEvent.setup();
    const router = createAppRouter({ initialEntries: ['/'] });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main-content');

    await user.tab();
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveFocus();
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute(
      'href',
      '#main-content',
    );
  });

  it('recovers from an unknown route through visible navigation', async () => {
    const user = userEvent.setup();
    const router = createAppRouter({ initialEntries: ['/missing'] });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Page not found' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'Return to library' }));

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
  });

  it('announces pending route navigation', async () => {
    let finishLoading: (() => void) | undefined;
    const loader = new Promise<void>((resolve) => {
      finishLoading = resolve;
    });
    const router = createAppRouter({
      initialEntries: ['/library'],
      additionalRoutes: [
        {
          path: 'slow',
          loader: () => loader,
          element: <h1>Loaded route</h1>,
        },
      ],
    });

    render(<RouterProvider router={router} />);
    await screen.findByRole('heading', { name: 'Library' });

    const navigation = router.navigate('/slow');

    expect(
      await screen.findByRole('status', { name: 'Page loading' }),
    ).toHaveTextContent('Loading page');

    finishLoading?.();
    await navigation;

    expect(
      await screen.findByRole('heading', { name: 'Loaded route' }),
    ).toBeInTheDocument();
  });

  it('announces and focuses the destination heading after route changes', async () => {
    const router = createAppRouter({ initialEntries: ['/library'] });

    render(<RouterProvider router={router} />);
    await screen.findByRole('heading', { name: 'Library' });

    await router.navigate('/albums');

    const heading = await screen.findByRole('heading', { name: 'Albums' });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(
      screen.getByRole('status', { name: 'Current page' }),
    ).toHaveTextContent('Albums');
  });

  it('renders a useful boundary when a route loader fails', async () => {
    const router = createAppRouter({
      initialEntries: ['/broken'],
      additionalRoutes: [
        {
          path: 'broken',
          loader: () => {
            throw new Response('Unavailable', {
              status: 503,
              statusText: 'Service Unavailable',
            });
          },
          element: <h1>Never rendered</h1>,
        },
      ],
    });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Something went wrong' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/503 Service Unavailable/)).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Return to library' }),
    ).toBeInTheDocument();
  });

  it('uses the configured base path and identifies the API-free static preview', async () => {
    vi.stubEnv('BASE_URL', '/Vistara/');
    vi.stubEnv('MODE', 'pages');
    const router = createAppRouter({ initialEntries: ['/Vistara/'] });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/Vistara/library');
    expect(
      screen.getByRole('complementary', { name: 'Static preview notice' }),
    ).toHaveTextContent(
      'Static preview only—no API, authentication, uploads, persistence, or worker processing.',
    );
    for (const brandLink of screen.getAllByRole('link', {
      name: 'Vistara library',
    })) {
      expect(brandLink).toHaveAttribute('href', '/Vistara/library');
    }
  });

  it('does not show the static preview notice in the normal application', async () => {
    vi.stubEnv('BASE_URL', '/');
    vi.stubEnv('MODE', 'production');
    const router = createAppRouter({ initialEntries: ['/'] });

    render(<RouterProvider router={router} />);

    expect(
      await screen.findByRole('heading', { name: 'Library' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('complementary', { name: 'Static preview notice' }),
    ).not.toBeInTheDocument();
  });

  it('leaves focus alone when only the query string changes', async () => {
    const router = createAppRouter({ initialEntries: ['/library'] });

    render(<RouterProvider router={router} />);
    await screen.findByRole('heading', { name: 'Library' });

    const search = screen.getAllByRole('link', { name: /Search/ })[0]!;
    search.focus();

    await router.navigate('/library?view=list');

    await waitFor(() =>
      expect(router.state.location.search).toBe('?view=list'),
    );
    expect(search).toHaveFocus();
  });
});
